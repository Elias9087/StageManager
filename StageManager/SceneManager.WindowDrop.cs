using AsyncAwaitBestPractices;
using StageManager.Core;
using StageManager.Native;
using StageManager.Native.Interop;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StageManager
{
	/// <summary>A window of the stage that is dragged at its title bar, maybe into the sidebar.</summary>
	/// <param name="State">The state before the drag: where the window goes back to, with its size and state, before it is parked.</param>
	/// <param name="Remembered">The state was remembered before the drag. Otherwise it was read when the drag was handled and may be changed already.</param>
	/// <param name="WindowsOnStage">Windows of the scene on the stage when the drag started, the dragged one included.</param>
	public sealed record WindowDragSession(IWindow Window, WindowSnapshot State, bool Remembered, int WindowsOnStage)
	{
		/// <summary>The window rectangle before the drag.</summary>
		public Area Start => State.Rect;

		/// <summary>A window alone on the stage gets no card of its own: it would change nothing.</summary>
		public bool CanMakeOwnCard => WindowsOnStage > 1;
	}

	/// <summary>
	/// Dragging a window of the stage at its title bar into the sidebar, like on macOS: onto a card it goes into that scene, onto a free
	/// place it gets a card of its own. Afterwards it goes back to where the drag started and is parked with its new scene. The sidebar
	/// shows the target, see <see cref="WindowDropTargetAtEnd"/>.
	/// </summary>
	public partial class SceneManager
	{
		private WindowDragSession? _windowDrag;

		/// <summary>The last state of each window on the screen before the user moved it, see <see cref="WindowStateMemory"/>.</summary>
		private readonly WindowStateMemory _stageStates = new();

		/// <summary>The state before the last move that started: the window and what it was then.</summary>
		private (IntPtr Handle, WindowSnapshot State, bool Remembered)? _moveStart;

		/// <summary>The last place of each window in the layout of a snap group, for when it comes back into that group.</summary>
		private readonly Dictionary<IntPtr, SnapHome> _snapHomes = new();

		/// <summary>The window dragged at its title bar right now, null otherwise. Read by the sidebar to show the target.</summary>
		public WindowDragSession? WindowDrag => _windowDrag;

		/// <summary>Asked by the end of a drag: the target under the pointer and whether Escape was pressed during the drag.</summary>
		public Func<(WindowDropTarget Target, bool EscapeSeen)>? WindowDropTargetAtEnd { get; set; }

		/// <summary>Describes a target for the log.</summary>
		public string DescribeDropTarget(WindowDropTarget target) => target.Kind switch
		{
			WindowDropKind.Card when target.Card is Guid id => $"card {(_sceneById.TryGetValue(id, out var s) ? DescribeScene(s) : id.ToString())}",
			WindowDropKind.NewCard => "new card",
			_ => "none"
		};

		/// <summary>
		/// Remembers the state of a window on the screen after each change, and takes what it was before when the user starts to move it.
		/// Runs for every event, also while StageManager moves windows itself: a window it brings back is on the stage from then on.
		/// </summary>
		private void TrackWindowState(IWindow window, WindowUpdateType type)
		{
			var handle = window.Handle;

			switch (type)
			{
				case WindowUpdateType.MoveStart:
					var (state, remembered) = _stageStates.BeginMove(handle.ToInt64(), SnapshotOf(handle));
					_moveStart = (handle, state, remembered);
					break;

				case WindowUpdateType.MoveEnd:
					_stageStates.EndMove(handle.ToInt64());
					RememberState(handle);
					break;

				case WindowUpdateType.LocationChanged:
				case WindowUpdateType.MinimizeEnd:
				case WindowUpdateType.Foreground:
				case WindowUpdateType.Show:
					RememberState(handle);
					break;
			}
		}

		/// <summary>Only a window on the screen: a parked or minimized window keeps the state it had on the stage.</summary>
		private void RememberState(IntPtr handle)
		{
			if (IsOnScreen(handle))
				_stageStates.Remember(handle.ToInt64(), SnapshotOf(handle));
		}

		private static WindowSnapshot SnapshotOf(IntPtr handle)
		{
			var maximized = Win32.IsZoomed(handle);
			Area? normal = maximized && NativeMethods.TryGetNormalPosition(handle, out var n) ? new Area(n.left, n.top, n.right, n.bottom) : null;
			return new WindowSnapshot(RectOf(handle), maximized, IsArranged(handle), normal);
		}

		private static string Text(WindowSnapshot s) => $"rect={Text(s.Rect)} maximized={s.Maximized} arranged={s.Arranged}";

		private void HandleWindowDragStart(IWindow window)
		{
			_windowDrag = null;

			var scene = _current;
			if (scene is null || !scene.Windows.Any(w => w.Handle == window.Handle))
				return;

			if (!App.Settings.Current.Sidebar.DropWindowToCard)
				return;

			var handle = window.Handle;
			var rect = RectOf(handle);
			var buttonDown = WindowDragProbe.IsPrimaryButtonDown();
			int? hitTest = null;
			var titleBar = false;

			if (Win32.GetCursorPos(out var cursor))
			{
				hitTest = buttonDown ? WindowDragProbe.HitTest(handle, cursor.X, cursor.Y) : null;
				titleBar = WindowDragStart.IsTitleBarDrag(buttonDown, hitTest, rect, cursor.X, cursor.Y, WindowDragStart.CaptionBand * WindowDragProbe.ScaleOf(handle));
			}

			// what the window was before the drag. Windows changes it at once, before this event is handled
			var (state, remembered) = _moveStart is { } start && start.Handle == handle ? (start.State, start.Remembered) : (SnapshotOf(handle), false);
			var onStage = scene.Windows.Count();

			DebugLog.Write("DROP", $"start {DebugLog.Describe(handle)} titleBar={titleBar} hitTest={(hitTest is int h ? h.ToString() : "-")} button={buttonDown} "
				+ $"windowsOnStage={onStage} snapGroup={IsSnapGroup(scene)}");
			DebugLog.Write("DROP", $"start state ({(remembered ? "remembered before the drag" : "read now, nothing remembered")}): {Text(state)}, now: {Text(SnapshotOf(handle))}");

			if (titleBar)
				_windowDrag = new WindowDragSession(window, state, remembered, onStage);
		}

		/// <returns>True if the window is dropped into the sidebar: the snap group must not handle the end of the move then.</returns>
		private bool HandleWindowDragEnd(IWindow window)
		{
			var session = _windowDrag;
			if (session is null || session.Window.Handle != window.Handle)
				return false;

			var (target, escapeSeen) = WindowDropTargetAtEnd?.Invoke() ?? (WindowDropTarget.None, false);
			_windowDrag = null;

			var end = RectOf(window.Handle);
			var outcome = WindowDragEnd.Decide(target, escapeSeen, session.Start, end);
			DebugLog.Write("DROP", $"end {DebugLog.Describe(window.Handle)} outcome={outcome} target={DescribeDropTarget(target)} escape={escapeSeen} start={Text(session.Start)} end={Text(end)}");

			if (outcome != WindowDropOutcome.Drop)
				return false;

			// the snap group check must not take the window out of the layout and put the scene of its app onto the stage
			_movedByUser.Remove(window.Handle);
			_moveStartRects.Remove(window.Handle);
			if (_snapMoveInProgress == window.Handle)
				_snapMoveInProgress = IntPtr.Zero;

			DropWindow(session, target).SafeFireAndForget(ex => Log.Error("Dropping a window into the sidebar failed", ex));
			return true;
		}

		/// <summary>A drag whose end never came (the window vanished, the event got lost): no target is shown any more.</summary>
		public void AbortWindowDrag(string reason)
		{
			if (_windowDrag is not WindowDragSession session)
				return;

			_windowDrag = null;
			DebugLog.Write("DROP", $"aborted {DebugLog.Describe(session.Window.Handle)}: {reason}");

			// without the end of the move the state of the window would never be remembered again
			_stageStates.EndMove(session.Window.Handle.ToInt64());
		}

		private async Task DropWindow(WindowDragSession session, WindowDropTarget target)
		{
			var window = session.Window;
			var scene = FindSceneForWindow(window);

			if (scene is null || !ReferenceEquals(scene, _current))
			{
				DebugLog.Write("DROP", $"{DebugLog.Describe(window.Handle)} is not on the stage any more, nothing to do");
				return;
			}

			var targetScene = target.Card is Guid id && _sceneById.TryGetValue(id, out var s) ? s : null;
			var plan = WindowDropRules.Plan(target, scene.Windows.Count(), targetIsStage: ReferenceEquals(targetScene, scene));
			if (target.Kind == WindowDropKind.Card && targetScene is null)
				plan = WindowDropPlan.Ignore;

			var wasSnapGroup = IsSnapGroup(scene);
			DebugLog.Write("DROP", $"plan={plan} {DebugLog.Describe(window.Handle)} from {DescribeScene(scene)} (snap group: {wasSnapGroup}) to {DescribeDropTarget(target)}");

			if (plan == WindowDropPlan.Ignore)
				return;

			// its place in the layout, for when it comes back into this group
			if (wasSnapGroup && scene.Layout.TryGetValue(window.Handle, out var place))
			{
				_snapHomes[window.Handle] = new SnapHome(scene.Id, place);
				DebugLog.Write("DROP", $"{DebugLog.Describe(window.Handle)} leaves its place {Text(place)} in the snap group");
			}

			RestoreDragStart(session);

			switch (plan)
			{
				case WindowDropPlan.OwnCard:
					await DetachWindow(window).ConfigureAwait(true);
					break;

				case WindowDropPlan.IntoCard:
					await MoveWindow(scene, window, targetScene!).ConfigureAwait(true);
					break;

				case WindowDropPlan.IntoCardAndSwitch:
					await MoveAloneWindowAndSwitch(scene, window, targetScene!).ConfigureAwait(true);
					break;
			}

			// the windows were moved by StageManager, that is no change of the layout of the snap group
			QuietLayoutChecks();

			// the dragged window had the focus and is out of sight now. A switch gives the focus to the new stage itself
			if (plan != WindowDropPlan.IntoCardAndSwitch && ReferenceEquals(_current, scene))
				FocusStage();

			var home = FindSceneForWindow(window);
			DebugLog.Write("DROP", $"result: {DebugLog.Describe(window.Handle)} is in {DescribeScene(home)}, parked={Parking.IsParked(window.Handle)}, "
				+ $"stage {DescribeScene(_current)} (snap group: {IsSnapGroup(_current)}), {_scenes.Count} scene(s)");
		}

		/// <summary>The window goes back to where the drag started, with its size and state, so it is parked from there and comes back there.</summary>
		private void RestoreDragStart(WindowDragSession session)
		{
			var handle = session.Window.Handle;

			// the place of the start is restored here, the move into another scene must not put it somewhere else
			if (session.Window is WindowsWindow ww)
				ww.PopLastLocation();

			DebugLog.Write("DROP", $"restore {DebugLog.Describe(handle)} before: {DebugLog.State(handle)}");

			try
			{
				_suspend = true;

				if (session.State.Maximized && session.State.Normal is Area normal)
				{
					NativeMethods.SetPlacement(handle, new RECT { left = (int)normal.Left, top = (int)normal.Top, right = (int)normal.Right, bottom = (int)normal.Bottom }, maximized: true);
				}
				else
				{
					var start = session.Start;
					Win32.SetWindowPos(handle, IntPtr.Zero, (int)start.Left, (int)start.Top, (int)start.Width, (int)start.Height,
						Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
				}
			}
			finally
			{
				_suspend = false;
			}

			DebugLog.Write("DROP", $"restore {DebugLog.Describe(handle)} after: {DebugLog.State(handle)} target: {Text(session.State)} ({(session.Remembered ? "remembered" : "read at the start")})");
		}

		/// <summary>
		/// The window was alone on the stage: it goes into the scene of the card, and that scene takes the stage, it would be empty otherwise.
		/// The window is parked first, so it comes back with the other windows of its new scene.
		/// </summary>
		private async Task MoveAloneWindowAndSwitch(Scene source, IWindow window, Scene target)
		{
			try
			{
				_suspend = true;

				var moved = _registry.Move(window.Handle.ToInt64(), target.Id);
				if (moved is null)
				{
					DebugLog.Write("DROP", $"{DebugLog.Describe(window.Handle)} could not be moved into {DescribeScene(target)}");
					return;
				}

				source.Remove(window);
				target.Add(window);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, window, ChangeType.Updated));

				if (moved.Value.SourceRemoved)
				{
					_scenes.Remove(source);
					_sceneById.Remove(source.Id);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Removed));
				}

				WindowStrategy.Hide(window);

				// a window that was part of the layout of this snap group before takes its place again if it is free
				RejoinSnapLayout(target, new[] { window });
			}
			finally
			{
				_suspend = false;
			}

			await SwitchTo(target).ConfigureAwait(true);
		}
	

		/// <summary>
		/// Windows that come into a snap group take their former place in its layout again, if they had one in this group and no other
		/// window of the layout lies there now. Otherwise they join the group without being part of the layout, with their own rectangle.
		/// Windows cannot be told to arrange a window, so it comes back at its place but not snapped.
		/// </summary>
		private void RejoinSnapLayout(Scene target, IEnumerable<IWindow> arrived)
		{
			if (!IsSnapGroup(target))
				return;

			foreach (var w in arrived)
			{
				var handle = w.Handle;
				SnapHome? home = _snapHomes.TryGetValue(handle, out var h) ? h : null;
				var others = target.Layout
					.Where(l => l.Key != handle && target.Windows.Any(m => m.Handle == l.Key))
					.Select(l => l.Value)
					.ToList();

				var shrink = SnapGeometry.LayoutShrink * WindowDragProbe.ScaleOf(handle);
				var decision = SnapRejoin.Decide(home, target.Id, target.Layout.ContainsKey(handle), others, shrink);
				var former = home is SnapHome f ? $", former place {Text(f.Rect)} in {(f.Group == target.Id ? "this" : "another")} snap group" : "";
				DebugLog.Write("REJOIN", $"{DebugLog.Describe(handle)} into {DescribeScene(target)}: {decision}{former}");

				if (decision != SnapRejoinDecision.Rejoin || home is not SnapHome place)
					continue;

				target.Layout[handle] = place.Rect;

				if (Parking.IsParked(handle))
				{
					var retargeted = Parking.Retarget(handle, place.Rect);
					DebugLog.Write("REJOIN", $"{DebugLog.Describe(handle)} comes back at {Text(place.Rect)}: {(retargeted ? "parking changed" : "parked maximized, it keeps its place")}");
				}
				else if (IsOnScreen(handle))
				{
					Win32.SetWindowPos(handle, IntPtr.Zero, (int)place.Rect.Left, (int)place.Rect.Top, (int)place.Rect.Width, (int)place.Rect.Height,
						Win32.SetWindowPosFlags.IgnoreZOrder | Win32.SetWindowPosFlags.DoNotActivate);
					DebugLog.Write("REJOIN", $"{DebugLog.Describe(handle)} put at {Text(place.Rect)}: {DebugLog.State(handle)}");
				}
				else
				{
					// minimized: brought back to its stored place when the group comes back, see RestoreSnapLayout
					DebugLog.Write("REJOIN", $"{DebugLog.Describe(handle)} is minimized, it gets its place when it is restored");
				}
			}
		}
	}
}
