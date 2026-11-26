using AsyncAwaitBestPractices;
using StageManager.Core;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace StageManager
{
	/// <summary>
	/// Windows of different scenes that are arranged together with the snap feature of Windows (dragging to the screen edge, Win+Arrow,
	/// the snap layouts of the maximize button, Snap Assist) become a snap group: a scene of its own that brings back the exact layout.
	/// The decisions are made in <see cref="SnapPlanner"/>, this part collects the facts from Windows and applies the result.
	/// </summary>
	public partial class SceneManager
	{
		/// <summary>How long to wait for a window chosen in Snap Assist to be arranged.</summary>
		private const int SnapJoinTimeoutMs = 500;
		private const int SnapJoinPollMs = 100;

		/// <summary>Snap Assist and resizing raise many events in a row, the snap group is checked once they calmed down.</summary>
		private const int SnapCheckDebounceMs = 300;

		/// <summary>After a switch parked windows and windows that were brought back may still be moving (asynchronously).</summary>
		private static readonly TimeSpan LayoutQuietTime = TimeSpan.FromMilliseconds(400);

		private int _foregroundVersion;
		private DispatcherTimer? _snapCheckTimer;
		private DispatcherTimer? _pendingJoinTimer;
		private IntPtr _snapMoveInProgress = IntPtr.Zero;
		private readonly Dictionary<IntPtr, Area> _moveStartRects = new();
		private readonly HashSet<IntPtr> _movedByUser = new();
		private readonly Dictionary<IntPtr, IWindow> _pendingJoins = new();
		private DateTime _layoutQuietUntil;

		private void InitializeSnapGroups()
		{
			var dispatcher = System.Windows.Application.Current.Dispatcher;

			_snapCheckTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(SnapCheckDebounceMs) };
			_snapCheckTimer.Tick += (_, _) => CheckSnapGroup().SafeFireAndForget(ex => Log.Error("Checking the snap group failed", ex));

			_pendingJoinTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(SnapCheckDebounceMs) };
			_pendingJoinTimer.Tick += (_, _) => JoinPendingWindows().SafeFireAndForget(ex => Log.Error("Joining snapped windows failed", ex));
		}

		private bool IsSnapGroup(Scene? scene) => scene is object && _registry.KindOf(scene.Id) == GroupKind.SnapGroup;

		/// <summary>Arranged (snapped) by Windows. Windows keeps that state while a window is minimized or out of the screen.</summary>
		private static bool IsArranged(IntPtr handle)
		{
			if (!Win32.IsWindowVisible(handle) || Win32.IsIconic(handle) || Win32.IsZoomed(handle))
				return false;

			try
			{
				return Win32.IsWindowArranged(handle);
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
		}

		/// <summary>Snapped and on the screen.</summary>
		private static bool IsSnapped(IntPtr handle) => IsOnScreen(handle) && IsArranged(handle);

		private static long MonitorOf(IntPtr handle) => Win32.MonitorFromWindow(handle, Win32.MONITOR_DEFAULTTONEAREST).ToInt64();

		private static Area RectOf(IntPtr handle)
		{
			var rect = new Win32.Rect();
			Win32.GetWindowRect(handle, ref rect);
			return new Area(rect.Left, rect.Top, rect.Right, rect.Bottom);
		}

		private static Area FrameOfWindow(IntPtr handle) => WindowGeometry.FrameOf(handle) ?? RectOf(handle);

		private static string Text(Area a) => $"({a.Left},{a.Top},{a.Right},{a.Bottom})";

		private SnapWindow ToSnapWindow(IWindow window) =>
			new(window.Handle.ToInt64(), FrameOfWindow(window.Handle), IsSnapped(window.Handle), MonitorOf(window.Handle), IsSnapGroup(FindSceneForWindow(window)));

		private IWindow? WindowOf(long handle) =>
			_scenes?.SelectMany(s => s.Windows).FirstOrDefault(w => w.Handle.ToInt64() == handle)
			?? GetSceneableWindows()?.FirstOrDefault(w => w.Handle.ToInt64() == handle);

		/// <summary>Layout changes are ignored for a moment, the windows are moved by StageManager itself.</summary>
		private void QuietLayoutChecks() => _layoutQuietUntil = DateTime.UtcNow + LayoutQuietTime;

		private bool IsLayoutQuiet => IsSwitching || DateTime.UtcNow < _layoutQuietUntil;

		#region Joining

		/// <summary>
		/// A window got the focus. Usually its scene takes the stage. If the stage has snapped windows, the window may have been chosen in
		/// Snap Assist: then the switch waits until it is clear whether the window was snapped next to them.
		/// </summary>
		private void HandleForeground(IWindow window)
		{
			var version = ++_foregroundVersion;

			if (!MightJoinSnapGroup(window, out var reason))
			{
				DebugLog.Write("JOIN", $"v{version} {DebugLog.Describe(window.Handle)} no candidate ({reason}), switching");
				SwitchToSceneByWindow(window).SafeFireAndForget(ex => Log.Error("Switching to the scene of a window failed", ex));
				return;
			}

			DebugLog.Write("JOIN", $"v{version} {DebugLog.Describe(window.Handle)} candidate, waiting for the arrangement");
			JoinOrSwitchAsync(window, version).SafeFireAndForget(ex => Log.Error("Joining a snap group failed", ex));
		}

		private bool MightJoinSnapGroup(IWindow window, out string reason)
		{
			reason = "";

			if (_current is null)
			{
				reason = "no scene on the stage";
				return false;
			}

			if (_current.Windows.Any(w => w.Handle == window.Handle))
			{
				reason = "already in the current scene";
				return false;
			}

			if (!_current.Windows.Any(w => IsSnapped(w.Handle)))
			{
				reason = "no arranged windows on the stage";
				return false;
			}

			return true;
		}

		private async Task JoinOrSwitchAsync(IWindow window, int version)
		{
			var waited = 0;
			while (waited < SnapJoinTimeoutMs && !IsSnapped(window.Handle))
			{
				await Task.Delay(SnapJoinPollMs).ConfigureAwait(true);
				waited += SnapJoinPollMs;
				DebugLog.Write("JOIN", $"v{version} poll {waited} ms {DebugLog.Describe(window.Handle)} {DebugLog.State(window.Handle)}");

				if (version != _foregroundVersion)
				{
					DebugLog.Write("JOIN", $"v{version} cancelled, newer focus change v{_foregroundVersion}");
					return;
				}
			}

			if (IsSnapped(window.Handle))
			{
				// Windows may still be placing the window
				await Task.Delay(SnapJoinPollMs).ConfigureAwait(true);

				if (version != _foregroundVersion)
				{
					DebugLog.Write("JOIN", $"v{version} cancelled after the arrangement, newer focus change v{_foregroundVersion}");
					return;
				}

				if (await TryJoinSnapGroup(window).ConfigureAwait(true))
					return;
			}
			else
			{
				DebugLog.Write("JOIN", $"v{version} timeout, the window was not arranged");
			}

			DebugLog.Write("JOIN", $"v{version} normal switch");
			await SwitchToSceneByWindow(window).ConfigureAwait(true);
		}

		/// <summary>
		/// Snap Assist keeps the focus until the last zone is filled, the windows chosen for the zones before never get it. They are
		/// recognized by being restored or moved to an arranged place while the stage has snapped windows.
		/// </summary>
		private void HandlePossibleSnapAssistPlacement(IWindow window)
		{
			if (IsLayoutQuiet || _current is null || _pendingJoins.ContainsKey(window.Handle))
				return;

			if (_current.Windows.Any(w => w.Handle == window.Handle))
				return;

			if (!IsSnapped(window.Handle) || !MightJoinSnapGroup(window, out _))
				return;

			DebugLog.Write("PENDING", $"arranged without the focus {DebugLog.Describe(window.Handle)} {DebugLog.State(window.Handle)}");
			_pendingJoins[window.Handle] = window;

			_pendingJoinTimer?.Stop();
			_pendingJoinTimer?.Start();
		}

		private async Task JoinPendingWindows()
		{
			_pendingJoinTimer?.Stop();

			var pending = _pendingJoins.Values.ToArray();
			_pendingJoins.Clear();

			foreach (var window in pending)
			{
				// it may have joined together with another window already
				if (_current is null || _current.Windows.Any(w => w.Handle == window.Handle))
					continue;

				DebugLog.Write("PENDING", $"trying to join {DebugLog.Describe(window.Handle)}");

				// no switch if it fails: the window did not get the focus
				await TryJoinSnapGroup(window).ConfigureAwait(true);
			}
		}

		/// <summary>Adds a window, and the snapped windows next to it, to the snapped windows of the stage if it touches them.</summary>
		private async Task<bool> TryJoinSnapGroup(IWindow window)
		{
			var stageScene = _current;
			if (stageScene is null)
				return JoinFailed(window, "no scene on the stage");

			if (IsSwitching)
				return JoinFailed(window, "a switch is under way");

			var stage = new SnapStage(stageScene.Id, IsSnapGroup(stageScene), _registry.MonitorOf(stageScene.Id), stageScene.Windows.Select(ToSnapWindow).ToList());

			// a window of another snap group brings its group along, the groups are merged
			SnapGroupInfo? windowGroup = null;
			if (FindSceneForWindow(window) is Scene windowScene && !ReferenceEquals(windowScene, stageScene) && IsSnapGroup(windowScene))
			{
				windowGroup = new SnapGroupInfo(windowScene.Id, _registry.MonitorOf(windowScene.Id), windowScene.Windows
					.Select(m => (m.Handle.ToInt64(), IsOnScreen(m.Handle) || !windowScene.Layout.TryGetValue(m.Handle, out var stored) ? RectOf(m.Handle) : stored))
					.ToList());
			}

			var candidates = (GetSceneableWindows() ?? Enumerable.Empty<IWindow>())
				.Where(w => w.Handle != window.Handle && IsOnScreen(w.Handle))
				.Select(ToSnapWindow)
				.ToList();

			var me = ToSnapWindow(window);
			DebugLog.Write("JOIN", $"try {DebugLog.Describe(window.Handle)} frame={Text(me.Frame)} arranged={me.Arranged} monitor={me.Monitor} stage: "
				+ string.Join("; ", stage.Windows.Select(w => $"0x{w.Handle:X} frame={Text(w.Frame)} arranged={w.Arranged}"))
				+ (windowGroup is null ? "" : $" own group: {windowGroup.Members.Count} window(s)"));

			var plan = SnapPlanner.PlanJoin(me, stage, windowGroup, candidates);
			if (plan.Kind == SnapJoinKind.None)
				return JoinFailed(window, plan.Reason);

			DebugLog.Write("JOIN", $"{plan.Kind}: anchors=[{string.Join(", ", plan.Anchors.Select(h => $"0x{h:X}"))}] joining=[{string.Join(", ", plan.Joining.Select(h => $"0x{h:X}"))}] home=[{string.Join(", ", plan.ReturnHome.Select(h => $"0x{h:X}"))}]");

			switch (plan.Kind)
			{
				case SnapJoinKind.Create:
					return await CreateSnapGroup(window, plan, me.Monitor).ConfigureAwait(true);
				case SnapJoinKind.Extend:
					return ExtendSnapGroup(stageScene, plan);
				case SnapJoinKind.Merge:
					return await MergeIntoSnapGroup(plan).ConfigureAwait(true);
				default:
					return false;
			}
		}

		private async Task<bool> CreateSnapGroup(IWindow window, SnapJoinPlan plan, long monitor)
		{
			var handles = plan.Anchors.Concat(plan.Joining).ToList();
			var windows = handles.Select(WindowOf).OfType<IWindow>().ToList();
			Scene group;

			try
			{
				_suspend = true;

				if (_registry.CreateSnapGroup(handles, monitor) is not SnapGroupResult result)
					return JoinFailed(window, "the registry refused to create the group");

				SyncScenes(windows);
				group = _sceneById[result.GroupId];

				ForgetParkingOfWindowsOnScreen(windows);
				CaptureLayout(group);
				DebugLog.Write("JOIN", $"created snap group {DescribeScene(group)}");
			}
			finally
			{
				_suspend = false;
			}

			// the windows of the group are on the screen already, the other windows of the former scene step aside
			await SwitchTo(group).ConfigureAwait(true);
			return true;
		}

		private bool ExtendSnapGroup(Scene group, SnapJoinPlan plan)
		{
			var joining = plan.Joining.Select(WindowOf).OfType<IWindow>().ToList();

			try
			{
				_suspend = true;

				foreach (var w in joining)
					_registry.Move(w.Handle.ToInt64(), group.Id);

				SyncScenes(joining);
				ForgetParkingOfWindowsOnScreen(joining);
				CaptureLayout(group);
				DebugLog.Write("JOIN", $"extended snap group {DescribeScene(group)}");
			}
			finally
			{
				_suspend = false;
			}

			return true;
		}

		/// <summary>
		/// A window of another snap group was snapped next to the snapped windows of the stage. The new layout goes into that group,
		/// members of the group whose place it takes go back to the scene of their app.
		/// </summary>
		private async Task<bool> MergeIntoSnapGroup(SnapJoinPlan plan)
		{
			if (plan.TargetGroup is not Guid targetId || !_sceneById.TryGetValue(targetId, out var target))
				return false;

			var source = _current;
			var layout = plan.Anchors.Concat(plan.Joining).Select(WindowOf).OfType<IWindow>().ToList();
			var home = plan.ReturnHome.Select(WindowOf).OfType<IWindow>().ToList();

			try
			{
				_suspend = true;

				foreach (var w in home)
				{
					DebugLog.Write("JOIN", $"merge: {DebugLog.Describe(w.Handle)} lies under the new layout, back to the scene of its app");
					_registry.ReturnHome(w.Handle.ToInt64());
				}

				foreach (var w in layout)
				{
					if (_registry.FindGroupId(w.Handle.ToInt64()) != targetId)
						_registry.Move(w.Handle.ToInt64(), targetId);
				}

				SyncScenes(layout.Concat(home));

				if (source is object && !ReferenceEquals(source, target) && _scenes.Contains(source))
					DissolveIfTooSmall(source, switchToRemainingWindow: false);

				ForgetParkingOfWindowsOnScreen(layout);
				CaptureLayout(target);
				DebugLog.Write("JOIN", $"merged into snap group {DescribeScene(target)}");
			}
			finally
			{
				_suspend = false;
			}

			// members that stayed in the group come back, the other windows step aside
			await SwitchTo(target).ConfigureAwait(true);
			return true;
		}

		private static bool JoinFailed(IWindow window, string reason)
		{
			DebugLog.Write("JOIN", $"failed {DebugLog.Describe(window.Handle)}: {reason}");
			return false;
		}

		/// <summary>
		/// A parked window chosen in Snap Assist was put on the screen by Windows. It is not out of the screen any more, so it must not
		/// be moved back to its old place later.
		/// </summary>
		private static void ForgetParkingOfWindowsOnScreen(IEnumerable<IWindow> windows)
		{
			foreach (var w in windows)
			{
				if (Parking.IsParked(w.Handle) && IsOnScreen(w.Handle))
				{
					DebugLog.Write("JOIN", $"forget the parking of {DebugLog.Describe(w.Handle)}, Windows put it on the screen");
					Parking.Forget(w.Handle);
				}
			}
		}

		#endregion

		#region Scenes

		/// <summary>
		/// Brings the scenes in line with the registry after windows changed their group there: moves the windows between the scenes,
		/// creates scenes for new groups, removes the ones of groups that are gone and tells the sidebar.
		/// </summary>
		private void SyncScenes(IEnumerable<IWindow> windows)
		{
			var created = new List<(Scene Scene, IWindow Window)>();
			var updated = new List<(Scene Scene, IWindow Window)>();
			var ids = _registry.GroupIds.ToList();

			foreach (var w in windows.Distinct())
			{
				if (_registry.FindGroupId(w.Handle.ToInt64()) is not Guid id)
					continue;

				var oldScene = _scenes.FirstOrDefault(s => s.Windows.Contains(w));

				if (!_sceneById.TryGetValue(id, out var newScene))
				{
					newScene = new Scene(id, GroupKeyOf(w));
					var position = ids.IndexOf(id);
					var index = _scenes.Count(s => ids.IndexOf(s.Id) is var i && i >= 0 && i < position);
					_scenes.Insert(Math.Min(index, _scenes.Count), newScene);
					_sceneById[id] = newScene;
					created.Add((newScene, w));
				}

				if (ReferenceEquals(oldScene, newScene))
					continue;

				if (oldScene is object)
				{
					oldScene.Remove(w);
					updated.Add((oldScene, w));
				}

				newScene.Add(w);
				updated.Add((newScene, w));
			}

			var removed = _scenes.Where(s => !ids.Contains(s.Id)).ToList();
			foreach (var s in removed)
			{
				_scenes.Remove(s);
				_sceneById.Remove(s.Id);
			}

			foreach (var (scene, window) in created)
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));

			foreach (var (scene, window) in updated.GroupBy(u => u.Scene).Select(g => g.First()))
			{
				if (!removed.Contains(scene) && !created.Any(c => ReferenceEquals(c.Scene, scene)))
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			foreach (var scene in removed)
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, updated.FirstOrDefault(u => ReferenceEquals(u.Scene, scene)).Window, ChangeType.Removed));
		}

		/// <summary>A snap group of less than two windows is no layout any more, its window goes back to the scene of its app.</summary>
		/// <returns>True if the group was dissolved.</returns>
		private bool DissolveIfTooSmall(Scene group, bool switchToRemainingWindow)
		{
			if (!IsSnapGroup(group))
				return false;

			var wasCurrent = ReferenceEquals(group, _current);
			var windows = group.Windows.ToList();

			if (_registry.DissolveIfTooSmall(group.Id).Count == 0)
				return false;

			DebugLog.Write("DISSOLVE", $"snap group with {windows.Count} window(s), wasCurrent={wasCurrent}: {DescribeScene(group)}");

			var suspended = _suspend;
			try
			{
				_suspend = true;
				SyncScenes(windows);
			}
			finally
			{
				_suspend = suspended;
			}

			if (switchToRemainingWindow && wasCurrent && windows.FirstOrDefault() is IWindow remaining && FindSceneForWindow(remaining) is Scene home)
				SwitchTo(home).SafeFireAndForget(ex => Log.Error("Switching after a snap group was dissolved failed", ex));

			return true;
		}

		/// <summary>A card was merged into another one: the stored layout of the windows goes along, the result is a snap group if either was.</summary>
		private static void TakeOverLayout(Scene source, Scene target)
		{
			foreach (var (handle, rect) in source.Layout)
				target.Layout[handle] = rect;
		}

		/// <summary>
		/// "Detach this window" on a card: the window gets a card of its own, also if other windows of its app stay on the card. New
		/// windows of the app still go to the card of the app. A window that leaves the stage is no longer shown.
		/// </summary>
		public Task DetachWindow(IWindow window)
		{
			var scene = FindSceneForWindow(window);
			if (scene is null)
				return Task.CompletedTask;

			var wasCurrent = ReferenceEquals(scene, _current);
			var wasSnapGroup = IsSnapGroup(scene);

			try
			{
				_suspend = true;

				if (_registry.Detach(window.Handle.ToInt64()) is null)
				{
					DebugLog.Write("DETACH", $"{DebugLog.Describe(window.Handle)} is alone on its card, nothing to do");
					return Task.CompletedTask;
				}

				DebugLog.Write("DETACH", $"{DebugLog.Describe(window.Handle)} out of {DescribeScene(scene)} (snap group: {wasSnapGroup}) onto a card of its own");

				SyncScenes(new[] { window });

				// a snap group of one window is no layout any more
				if (wasSnapGroup)
					DissolveIfTooSmall(scene, switchToRemainingWindow: wasCurrent);

				if (wasCurrent)
					WindowStrategy.Hide(window);
			}
			finally
			{
				_suspend = false;
			}

			return Task.CompletedTask;
		}

		/// <summary>
		/// The hotkey "detach the active window": the window in front leaves the stage and gets a card of its own, the other windows of
		/// the stage stay. A dialog stands for the window it belongs to. Nothing happens if the window is alone on the stage.
		/// </summary>
		public async Task DetachActiveWindow()
		{
			var foreground = Win32.GetForegroundWindow();
			var root = Win32.GetAncestor(foreground, Win32.GA.GA_ROOTOWNER);
			var scene = _current;

			var window = WindowOf(foreground.ToInt64()) ?? (root != IntPtr.Zero ? WindowOf(root.ToInt64()) : null);
			if (scene is null || window is null || !ReferenceEquals(FindSceneForWindow(window), scene))
			{
				DebugLog.Write("DETACH", $"hotkey: {DebugLog.Describe(foreground)} is not a window of the stage, nothing to do");
				return;
			}

			if (scene.Windows.Count() < 2)
			{
				DebugLog.Write("DETACH", $"hotkey: {DebugLog.Describe(window.Handle)} is alone on the stage, nothing to do");
				return;
			}

			DebugLog.Write("DETACH", $"hotkey: {DebugLog.Describe(window.Handle)} leaves the stage");
			await DetachWindow(window).ConfigureAwait(true);

			// the window that had the focus is out of sight now
			FocusStage();
		}

		/// <summary>"Separate" on the card of a snap group: the windows of the app go back to the card of their app.</summary>
		private void SplitAppFromSnapGroup(Scene group, string appKey)
		{
			var windows = group.Windows.Where(w => string.Equals(GroupKeyOf(w), appKey, StringComparison.OrdinalIgnoreCase)).ToList();
			if (windows.Count == 0 || windows.Count == group.Windows.Count())
				return;

			var wasCurrent = ReferenceEquals(group, _current);
			DebugLog.Write("SPLIT", $"app {appKey} out of the snap group {DescribeScene(group)}, back to the card of its app");

			try
			{
				_suspend = true;

				foreach (var w in windows)
					_registry.ReturnHome(w.Handle.ToInt64());

				SyncScenes(windows);
				DissolveIfTooSmall(group, switchToRemainingWindow: wasCurrent);

				// an app that leaves the stage is no longer shown
				if (wasCurrent)
				{
					foreach (var w in windows)
						WindowStrategy.Hide(w);
				}
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>
		/// Stores where the windows of the layout are. Windows that are not on the screen keep their stored place, windows that never
		/// were snapped (added by dragging a card onto the group) do not become part of the layout.
		/// </summary>
		private void CaptureLayout(Scene group)
		{
			foreach (var w in group.Windows)
			{
				if (!IsOnScreen(w.Handle))
					continue;

				if (IsArranged(w.Handle) || group.Layout.ContainsKey(w.Handle))
					group.Layout[w.Handle] = RectOf(w.Handle);
			}

			// the place of each window in the layout, for when it comes back after it left the group
			foreach (var w in group.Windows)
			{
				if (group.Layout.TryGetValue(w.Handle, out var place))
					_snapHomes[w.Handle] = new SnapHome(group.Id, place);
			}

			DebugLog.Write("LAYOUT", $"captured {string.Join("; ", group.Layout.Select(l => $"{DebugLog.Describe(l.Key)} {Text(l.Value)}"))}");
		}

		#endregion

		#region Layout changes

		private bool IsCurrentSnapGroupWindow(IWindow window) => IsSnapGroup(_current) && _current!.Windows.Any(w => w.Handle == window.Handle);

		private void HandleSnapGroupMoveStart(IWindow window)
		{
			if (!IsCurrentSnapGroupWindow(window))
				return;

			_snapMoveInProgress = window.Handle;

			// the stored layout is the better reference: the event arrives asynchronously, the window may have moved already
			var reference = _current!.Layout.TryGetValue(window.Handle, out var stored) ? stored : RectOf(window.Handle);
			_moveStartRects[window.Handle] = reference;

			DebugLog.Write("CHECK", $"move start {DebugLog.Describe(window.Handle)} reference={Text(reference)} {DebugLog.State(window.Handle)}");
		}

		private void HandleSnapGroupLocationChanged(IWindow window, bool isMoveEnd)
		{
			if (!IsCurrentSnapGroupWindow(window))
				return;

			if (isMoveEnd)
			{
				_movedByUser.Add(window.Handle);

				if (_snapMoveInProgress == window.Handle)
					_snapMoveInProgress = IntPtr.Zero;
			}
			else if (IsLayoutQuiet)
			{
				return;
			}

			_snapCheckTimer?.Stop();
			_snapCheckTimer?.Start();
		}

		/// <summary>
		/// Checks whether windows left the snap group on the stage by being maximized or dragged at the title bar. Resizing never changes
		/// the group. A window that leaves goes back to the scene of its app, which takes the stage.
		/// </summary>
		private async Task CheckSnapGroup()
		{
			_snapCheckTimer?.Stop();

			// the end of the move starts the check again
			if (_snapMoveInProgress != IntPtr.Zero)
				return;

			var group = _current;
			if (!IsSnapGroup(group) || IsSwitching)
			{
				ResetSnapMoveTracking();
				return;
			}

			var members = group!.Windows
				.Select(w => new SnapMember(
					w.Handle.ToInt64(),
					group.Layout.ContainsKey(w.Handle),
					Win32.IsZoomed(w.Handle),
					_movedByUser.Contains(w.Handle),
					_moveStartRects.TryGetValue(w.Handle, out var reference) ? reference : null,
					RectOf(w.Handle)))
				.ToList();

			foreach (var m in members)
				DebugLog.Write("CHECK", $"  0x{m.Handle:X} inLayout={m.InLayout} maximized={m.Maximized} moved={m.MovedByUser} reference={(m.Reference is Area r ? Text(r) : "-")} now={Text(m.Current)}");

			var leavingHandles = SnapPlanner.Leaving(members);
			var moved = _movedByUser.ToHashSet();
			ResetSnapMoveTracking();

			if (leavingHandles.Count == 0)
			{
				CaptureLayout(group);
				return;
			}

			var leaving = group.Windows.Where(w => leavingHandles.Contains(w.Handle.ToInt64())).ToList();
			DebugLog.Write("CHECK", $"leaving: {string.Join(", ", leaving.Select(w => DebugLog.Describe(w.Handle)))}");

			try
			{
				_suspend = true;

				foreach (var w in leaving)
					_registry.ReturnHome(w.Handle.ToInt64());

				SyncScenes(leaving);

				if (_scenes.Contains(group) && !DissolveIfTooSmall(group, switchToRemainingWindow: false))
					CaptureLayout(group);
			}
			finally
			{
				_suspend = false;
			}

			// the window taken out of the layout stays on the screen, the scene of its app takes the stage
			var focus = leaving.FirstOrDefault(w => moved.Contains(w.Handle))
				?? leaving.FirstOrDefault(w => w.IsFocused)
				?? leaving.First();

			await SwitchToSceneByWindow(focus).ConfigureAwait(true);
		}

		private void ResetSnapMoveTracking()
		{
			_movedByUser.Clear();
			_moveStartRects.Clear();
		}

		#endregion

		#region Restoring

		/// <summary>
		/// Puts windows of a snap group that were minimized (they could not be parked) back to their stored place if Windows restored them
		/// without their snap state. Windows that are still arranged stay untouched: moving them would break the snap group of Windows.
		/// </summary>
		private void RestoreSnapLayout(Scene group, ISet<IntPtr> restoredFromMinimized)
		{
			foreach (var w in group.Windows)
			{
				if (!restoredFromMinimized.Contains(w.Handle) || !group.Layout.TryGetValue(w.Handle, out var target))
					continue;

				if (!IsOnScreen(w.Handle) || IsArranged(w.Handle) || SnapGeometry.IsSame(target, RectOf(w.Handle)))
					continue;

				DebugLog.Write("RESTORE", $"{DebugLog.Describe(w.Handle)} before: {DebugLog.State(w.Handle)} target={Text(target)}");
				Win32.SetWindowPos(w.Handle, IntPtr.Zero, (int)target.Left, (int)target.Top, (int)target.Width, (int)target.Height,
					Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
				DebugLog.Write("RESTORE", $"{DebugLog.Describe(w.Handle)} after: {DebugLog.State(w.Handle)}");
			}
		}

		#endregion
	}
}
