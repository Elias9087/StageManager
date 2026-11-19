using AsyncAwaitBestPractices;
using StageManager.Core;
using StageManager.Native;
using StageManager.Native.Interop;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StageManager
{
	public partial class SceneManager
	{
		private readonly Desktop _desktop;
		private readonly DesktopIconsPolicy _desktopIcons;
		private readonly SceneRegistry _registry = new();
		private readonly Dictionary<Guid, Scene> _sceneById = new();
		private List<Scene> _scenes;
		private Scene _current;
		private bool _suspend = false;

		public event EventHandler<SceneChangedEventArgs> SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs> CurrentSceneSelectionChanged;

		private IWindowStrategy WindowStrategy { get; } = new ScreenOffsetWindowStrategy(); // alternatives: NormalizeAndMinimizeWindowStrategy/OpacityWindowStrategy/ShowAndHideWindowStrategy

		public WindowsManager WindowsManager { get; }

		/// <summary>The scene on the stage. Null in the desktop view.</summary>
		public Scene Current => _current;

		public SceneManager(WindowsManager windowsManager)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_desktop = new Desktop();
			_desktopIcons = new DesktopIconsPolicy(
				_desktop,
				new FileDesktopIconsState(System.IO.Path.Combine(AppPaths.DataDirectory, "desktop-icons.state")),
				() => App.Settings.Current.DesktopIcons);
			_desktopIcons.Start();
			_desktopIcons.Apply(sceneActive: true);
		}

		// the list of excluded apps the windows were sorted by the last time
		private IReadOnlyList<string> _knownExcluded = Array.Empty<string>();

		/// <summary>
		/// Applies a changed list of excluded apps right away: the windows of an app that is excluded now leave their cards and come back
		/// to the screen if they were hidden, the windows of an app that is not excluded any more get a card.
		/// </summary>
		public void ApplyExcludedApps()
		{
			var current = App.Settings.Current.Stage.ExcludedApps.ToList();
			var previous = _knownExcluded;

			if (previous.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
				return;

			_knownExcluded = current;

			if (_scenes is null)
				return;

			var (removed, added) = WindowsManager.Reevaluate(previous);

			foreach (var window in removed)
			{
				// a hidden window is out of the screen, nobody would bring it back once it is forgotten
				if (Parking.IsParked(window.Handle))
					WindowStrategy.Show(window);

				WindowsManager_WindowDestroyed(window);
			}

			Scene? last = null;
			foreach (var window in added)
			{
				var placed = PlaceWindow(window, companion: false);
				if (placed.Switch)
					last = placed.Scene;
			}

			// it is on the screen already, its scene takes the stage like a scene of a window that was just opened
			if (last is object)
				SwitchTo(last).SafeFireAndForget(ex => Log.Error("Taking an app back in failed", ex));
		}

		/// <summary>Applies a changed desktop icon setting right away.</summary>
		public void ApplyDesktopIconSettings() => _desktopIcons.Reapply();

		/// <summary>The Explorer was restarted and may show the icons again.</summary>
		public void ResyncDesktopIcons() => _desktopIcons.Resync();

		public async Task Start()
		{
			// the WinEvent hooks only fire on a thread with a message loop, that is the UI thread (its managed id is not guaranteed to be 1)
			if (System.Windows.Application.Current?.Dispatcher.CheckAccess() != true)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			_knownExcluded = App.Settings.Current.Stage.ExcludedApps.ToList();

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.UntrackedFocus += WindowsManager_UntrackedFocus;

			InitializeSnapGroups();

			// the window that has the focus at start: it may vanish before any focus change was reported
			_focus.Activated(Win32.GetForegroundWindow().ToInt64());

			await WindowsManager.Start();
		}

		internal void Stop()
		{
			WindowsManager.Stop();

			foreach (var scene in _scenes ?? new List<Scene>())
			{
				foreach (var w in scene.Windows)
					WindowStrategy.Show(w);
			}

			// whatever the scenes know about, nothing may stay out of the screen
			Parking.RestoreAll();
			WindowRescue.Run();

			_desktopIcons.Stop();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			// TEMPORARY: events that matter for snap groups
			if (type is WindowUpdateType.Foreground or WindowUpdateType.MoveStart or WindowUpdateType.MoveEnd
				or WindowUpdateType.MinimizeStart or WindowUpdateType.MinimizeEnd)
				DebugLog.Write("EVENT", $"{type} suspend={_suspend} {DebugLog.Describe(window.Handle)} {DebugLog.State(window.Handle)}");

			// minimizing the active window makes Windows hand the focus to another one
			if (type == WindowUpdateType.MinimizeStart)
				_focus.Vanished(window.Handle.ToInt64());

			if (_suspend)
			{
				if (type == WindowUpdateType.Foreground)
					_focus.Activated(window.Handle.ToInt64());

				return;
			}

			if (type == WindowUpdateType.Foreground)
			{
				var handoff = IsFocusHandoff(window);
				var mightBeHandoff = !handoff && MightBeFocusHandoff(window);
				_focus.Activated(window.Handle.ToInt64());
				var focusVersion = ++_focusVersion;

				if (handoff)
				{
					// Windows gave the focus to a window out of sight because the active one vanished: the stage stays, its front window gets the focus
					DebugLog.Write("FOCUS", $"handoff to {DebugLog.Describe(window.Handle)} ignored, the stage keeps the focus");
					FocusStage();
					return;
				}

				if (mightBeHandoff)
				{
					// Windows reports the new active window before the old one is minimized: decide a moment later
					DebugLog.Write("FOCUS", $"{DebugLog.Describe(window.Handle)} out of sight got the focus, deciding in {FocusHandoff.DecisionDelay.TotalMilliseconds} ms");
					DecideFocusLater(window, focusVersion).SafeFireAndForget(ex => Log.Error("Deciding about a focus change failed", ex));
					return;
				}

				// the window may have been snapped next to the windows of the stage, then it joins them instead of switching
				HandleForeground(window);
				return;
			}

			switch (type)
			{
				case WindowUpdateType.MoveStart:
					HandleSnapGroupMoveStart(window);
					break;
				case WindowUpdateType.MoveEnd:
					HandleSnapGroupLocationChanged(window, isMoveEnd: true);
					break;
				case WindowUpdateType.LocationChanged:
					HandleSnapGroupLocationChanged(window, isMoveEnd: false);
					HandlePossibleSnapAssistPlacement(window);
					break;
				case WindowUpdateType.MinimizeEnd:
					HandlePossibleSnapAssistPlacement(window);
					break;
			}
		}

		private readonly FocusHandoff _focus = new();
		private int _focusVersion;

		private bool MightBeFocusHandoff(IWindow window)
		{
			var current = _current;
			if (current is null)
				return false;

			return FocusHandoff.MightBeHandoff(
				targetOnScreen: IsOnScreen(window.Handle),
				targetInCurrentScene: current.Windows.Any(w => w.Handle == window.Handle),
				currentSceneHasWindowOnScreen: current.Windows.Any(w => IsOnScreen(w.Handle)));
		}

		private async Task DecideFocusLater(IWindow window, int focusVersion)
		{
			await Task.Delay(FocusHandoff.DecisionDelay).ConfigureAwait(true);

			// the focus moved on in the meantime, the newer change decides
			if (focusVersion != _focusVersion || _suspend)
			{
				DebugLog.Write("FOCUS", $"decision about {DebugLog.Describe(window.Handle)} dropped, a newer focus change or switch came in");
				return;
			}

			if (IsFocusHandoff(window))
			{
				DebugLog.Write("FOCUS", $"handoff to {DebugLog.Describe(window.Handle)} ignored (reported before the active window vanished), the stage keeps the focus");
				FocusStage();
				return;
			}

			DebugLog.Write("FOCUS", $"{DebugLog.Describe(window.Handle)} was activated by the user, switching");
			HandleForeground(window);
		}

		private bool IsFocusHandoff(IWindow window)
		{
			var current = _current;
			if (current is null)
				return false;

			return _focus.IsHandoff(
				targetOnScreen: IsOnScreen(window.Handle),
				targetInCurrentScene: current.Windows.Any(w => w.Handle == window.Handle),
				currentSceneHasWindowOnScreen: current.Windows.Any(w => IsOnScreen(w.Handle)));
		}

		/// <summary>
		/// Visible on a monitor: not hidden, not minimized and not out of the screen. Decided by where the window is, not by whether it is
		/// parked: Windows or the user may have put a parked window on the screen, and a window that was just brought back may still be
		/// on its way (the move is asynchronous).
		/// The real window rectangle counts: WindowGeometry.FrameOf reports the work area for every maximized window, wherever it is,
		/// and maximized windows are parked as they are.
		/// </summary>
		private static bool IsOnScreen(IntPtr handle)
		{
			if (!Win32.IsWindowVisible(handle) || Win32.IsIconic(handle))
				return false;

			var rect = RectOf(handle);
			return System.Windows.Forms.Screen.AllScreens.Any(s => FlightPlanner.Intersects(new Area(s.Bounds.Left, s.Bounds.Top, s.Bounds.Right, s.Bounds.Bottom), rect));
		}

		/// <summary>Gives the focus to the front window of the stage that is on the screen.</summary>
		private void FocusStage()
		{
			var scene = _current;
			if (scene is null)
				return;

			var onScreen = scene.Windows.Where(w => IsOnScreen(w.Handle)).ToList();
			if (onScreen.Count == 0)
				return;

			var topToBottom = WindowStack.TopToBottom().ToList();
			var front = onScreen
				.OrderBy(w => topToBottom.IndexOf(w.Handle.ToInt64()) is var i && i >= 0 ? i : int.MaxValue)
				.First();

			DebugLog.Write("FOCUS", $"focus the stage: {DebugLog.Describe(front.Handle)}");

			// not right inside the event of the other window, Windows may still be busy handing the focus on
			System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => front.Focus());
		}

		/// <summary>
		/// After a switch the focus may rest on a window out of sight: Windows hands it on when the active window is hidden. Typing would
		/// go to a window nobody sees, so the front window of the stage takes it. Waits a moment, because Windows does not hand it on at once.
		/// </summary>
		private async Task FocusStageIfStranded(Scene scene)
		{
			await Task.Delay(250).ConfigureAwait(true);

			if (!ReferenceEquals(_current, scene) || IsSwitching)
				return;

			var foreground = Win32.GetForegroundWindow();
			var active = GetSceneableWindows()?.FirstOrDefault(w => w.Handle == foreground);

			// the sidebar, the taskbar or the desktop: not a window out of sight
			if (active is null)
				return;

			if (FocusHandoff.IsStranded(
				activeOnScreen: IsOnScreen(active.Handle),
				activeInCurrentScene: scene.Windows.Any(w => w.Handle == active.Handle),
				currentSceneHasWindowOnScreen: scene.Windows.Any(w => IsOnScreen(w.Handle))))
			{
				DebugLog.Write("FOCUS", $"after the switch the focus rests on {DebugLog.Describe(active.Handle)} out of sight");
				FocusStage();
			}
		}

		/// <summary>
		/// Windows of apps that run with more rights than StageManager do not move (UIPI) and would stay over every other scene. They are
		/// recognized at start, this catches the ones that were not: a parked window still at its place is not managed any more.
		/// </summary>
		/// <remarks>Runs once, shortly after the windows were parked by a switch, and compares where they are with their parking spot.</remarks>
		private async Task AbandonWindowsThatRefusedToMove(IReadOnlyList<IntPtr> parked)
		{
			// the move is asynchronous
			await Task.Delay(500).ConfigureAwait(true);

			foreach (var handle in parked)
			{
				if (_current?.Windows.Any(w => w.Handle == handle) == true)
					continue;

				if (Parking.OriginalRectOf(handle) is not Area original || Parking.ParkedRectOf(handle) is not Area spot)
					continue;

				var now = RectOf(handle);
				if (!Elevation.RefusedToMove(original, spot, now, ProcessInfo.IsHung(handle)))
					continue;

				DebugLog.Write("REFUSED", $"{DebugLog.Describe(handle)} did not move when it was parked: original={Text(original)} spot={Text(spot)} now={Text(now)}, it is not managed any more");
				Parking.Forget(handle);
				WindowsManager.Abandon(handle);
			}
		}

		private void WindowsManager_UntrackedFocus(object? sender, IntPtr e)
		{
			// raised for any event of a window that is not tracked, only a real focus change counts
			if (Win32.GetForegroundWindow() == e)
			{
				_focus.Activated(e.ToInt64());
				_focusVersion++;
			}

			if (_suspend)
				return;

			// a click on the wallpaper gives the focus to the desktop: like on macOS the windows step aside and the desktop shows
			var className = new System.Text.StringBuilder(256);
			Win32.GetClassName(e, className, className.Capacity);

			if (DesktopView.IsDesktopClass(className.ToString()))
				SwitchTo(null).SafeFireAndForget(ex => Log.Error("Showing the desktop failed", ex));
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			// a closed or hidden window that was active makes Windows hand the focus to another one
			if (!Win32.IsWindow(window.Handle) || !Win32.IsWindowVisible(window.Handle))
				_focus.Vanished(window.Handle.ToInt64());

			// a window that only hides or cloaks itself (Windows Terminal does) still exists. StageManager forgets it, so it must not
			// stay out of the screen: once it shows again nobody would bring it back
			if (Win32.IsWindow(window.Handle))
				Parking.Unpark(window.Handle);
			else
				Parking.Forget(window.Handle);

			var removal = _registry.Remove(window.Handle.ToInt64());
			if (removal.GroupId is not Guid id || !_sceneById.TryGetValue(id, out var scene))
				return;

			DebugLog.Write("REMOVE", $"{DebugLog.Describe(window.Handle)} groupRemoved={removal.GroupRemoved}");

			scene.Remove(window);

			if (!removal.GroupRemoved)
			{
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));

				// a snap group of one window is no layout any more
				DissolveIfTooSmall(scene, switchToRemainingWindow: ReferenceEquals(scene, _current));
			}
			else
			{
				_scenes.Remove(scene);
				_sceneById.Remove(id);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));
			}
		}

		public Scene FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene FindSceneForWindow(IntPtr handle)
		{
			var id = _registry.FindGroupId(handle.ToInt64());
			return id is Guid g && _sceneById.TryGetValue(g, out var scene) ? scene : null;
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		private async Task SwitchToSceneByWindow(IWindow window)
		{
			var scene = FindSceneForWindow(window);
			if (scene is null)
				scene = PlaceWindow(window, companion: false).Scene;

			// the window that was activated is in front: on the card, while the others fly in and once they landed.
			// On the stage the order is read from the screen when the scene leaves it
			if (!ReferenceEquals(scene, _current))
				Restack(scene, ZOrder.ToFront(HandlesOf(scene), new[] { window.Handle.ToInt64() }));

			await SwitchTo(scene);
		}

		private async Task SwitchToSceneByNewWindow(IWindow window)
		{
			var placed = PlaceWindow(window, IsCompanion(window));

			// a companion joins the current scene, nothing has to be hidden or shown for it
			if (!placed.Switch)
			{
				WindowStrategy.Show(window);
				return;
			}

			await SwitchTo(placed.Scene).ConfigureAwait(true);
		}

		private (Scene Scene, bool Switch) PlaceWindow(IWindow window, bool companion)
		{
			EnsureScenes();

			var placement = _registry.Place(window.Handle.ToInt64(), GroupKeyOf(window), companion);

			if (placement.Created)
			{
				var created = new Scene(placement.GroupId, GroupKeyOf(window), window);
				_scenes.Add(created);
				_sceneById[placement.GroupId] = created;
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(created, window, ChangeType.Created));
				return (created, placement.Switch);
			}

			var scene = _sceneById[placement.GroupId];
			if (!scene.Windows.Contains(window))
			{
				scene.Add(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			return (scene, placement.Switch);
		}

		private static bool IsCompanion(IWindow window) =>
			window is WindowsWindow w && AppIdentity.IsListed(App.Settings.Current.Stage.Companions, w.ProcessExecutable);

		/// <summary>Screen rectangle of the thumbnail area of a card in physical pixels, null if the card is not on screen. Set by the sidebar.</summary>
		public Func<Scene, Area?>? CardAreaOf { get; set; }

		/// <summary>How tall the card of a scene is in physical pixels. Cards are as tall as their windows. Set by the sidebar.</summary>
		public Func<Scene, double>? CardHeightOf { get; set; }

		/// <summary>Physical pixels per unit of the sidebar layout. Set by the sidebar.</summary>
		public double CardScale { get; set; } = 1;

		/// <summary>The sidebar window. Flying windows pass below it, a window that flies to the stage must not cover it. Set by the sidebar.</summary>
		public IntPtr SidebarHandle { get; set; }

		/// <summary>The windows of a scene that left the stage have arrived on its card, the card can show them now.</summary>
		public event EventHandler<Scene>? CardLanded;

		private Task _flight = Task.CompletedTask;
		private FlightOverlay? _overlay;

		/// <summary>The desktop is shown: a click on the wallpaper deselected the scene. False at start and as soon as a scene is selected.</summary>
		public bool IsDesktopView { get; private set; }

		/// <summary>Windows are flying on an overlay at the moment.</summary>
		public bool IsFlying => _overlay is not null;

		/// <summary>
		/// A switch is under way: the windows of the new scene are not on the stage yet, the ones of the old scene may be still. What is
		/// on the screen now says nothing about what will be there.
		/// </summary>
		public bool IsSwitching => _suspend || _overlay is not null;

		/// <param name="animate">False if the windows have been shown flying already, e.g. by dragging their card onto the stage.</param>
		public async Task SwitchTo(Scene? scene, bool animate = true)
		{
			// a switch that is still waiting for its overlay to show has to start flying first
			await _takeoff.ConfigureAwait(true);

			// a switch that comes in while windows still fly lets them land first
			if (_overlay is not null)
			{
				_overlay.Skip();
				await _flight.ConfigureAwait(true);
			}

			// windows that were closed to the tray or destroyed without an event are forgotten first, so that they cannot come back
			WindowsManager.ForgetVanishedWindows();

			if (scene is object && _scenes?.Contains(scene) != true)
			{
				DebugLog.Write("SWITCH", $"skipped, all windows of the scene are gone: {DescribeScene(scene)}");
				return;
			}

			if (!_registry.TrySwitch(scene?.Id))
				return;

			DebugLog.Write("SWITCH", $"{DescribeScene(_current)} -> {DescribeScene(scene)}");

			IsDesktopView = scene is null;

			var takeoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			List<IntPtr>? refusalCheck = null;

			try
			{
				_suspend = true;
				_takeoff = takeoff.Task;

				var otherWindows = GetSceneableWindows().Except(scene?.Windows ?? Array.Empty<IWindow>()).ToArray();

				var prior = _current;

				// the scene that leaves keeps the order its windows lie on the screen, its card and its next return show them that way
				if (prior is object)
					KeepScreenOrder(prior);

				// a snap group remembers its exact layout before its windows leave the screen
				if (prior is object && IsSnapGroup(prior))
					CaptureLayout(prior);
				_current = scene;

				foreach (var s in _scenes)
					s.IsSelected = s.Equals(scene);

				var animated = animate ? PrepareFlight(prior, scene, otherWindows) : null;

				// nothing may vanish before its flying copy is on the screen: not the windows, not the card in the sidebar
				if (animated is not null)
					await animated.Overlay.WhenOnScreen().ConfigureAwait(true);

				// with an animation the windows of the scene show up when the flight lands, not before it takes off
				if (scene is object && animated is null)
					ShowInOrder(scene.Windows);

				// where the windows were before they leave the screen: a window Windows refuses to move is not managed any more
				// only the windows parked by this switch, the others were parked before and are checked already
				var parkedBefore = otherWindows.Where(o => Parking.IsParked(o.Handle)).Select(o => o.Handle).ToHashSet();

				foreach (var o in otherWindows)
					WindowStrategy.Hide(o);

				refusalCheck = otherWindows.Select(o => o.Handle).Where(h => !parkedBefore.Contains(h) && Parking.IsParked(h)).ToList();

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current, animated?.PriorCardWaits == true));

				_desktopIcons.Apply(sceneActive: scene is object);

				if (animated is not null)
					await RunFlight(animated, scene!, prior, takeoff).ConfigureAwait(true);
			}
			finally
			{
				takeoff.TrySetResult();
				_suspend = false;

				// windows that were parked or brought back may still be moving (asynchronously): that is no change of a layout
				QuietLayoutChecks();
				LogSceneLater(scene);
			}

			if (scene is object)
				FocusStageIfStranded(scene).SafeFireAndForget(ex => Log.Error("Focusing the stage failed", ex));

			if (refusalCheck is { Count: > 0 })
				AbandonWindowsThatRefusedToMove(refusalCheck).SafeFireAndForget(ex => Log.Error("Checking the parked windows failed", ex));
		}

		/// <summary>TEMPORARY: the state of the windows on the stage once the switch has settled.</summary>
		private static void LogSceneLater(Scene? scene)
		{
			if (scene is null || !DebugLog.Enabled)
				return;

			var handles = scene.Windows.Select(w => w.Handle).ToArray();
			Task.Run(async () =>
			{
				await Task.Delay(800).ConfigureAwait(false);
				foreach (var h in handles)
					DebugLog.Write("STAGE", $"{DebugLog.Describe(h)} {DebugLog.State(h)}");
			});
		}

		private Task _takeoff = Task.CompletedTask;

		private sealed record PreparedFlight(FlightOverlay Overlay, IReadOnlyList<IntPtr> Windows, bool PriorCardWaits);

		/// <summary>
		/// Puts the windows of the scenes on a transparent overlay at the position they have now. Must run before the real windows
		/// are shown or hidden, because DWM keeps showing the picture of a window it takes a thumbnail of even after the window is minimized.
		/// </summary>
		private PreparedFlight? PrepareFlight(Scene? prior, Scene? scene, IWindow[] otherWindows)
		{
			if (scene is null)
				return null;

			var style = SystemAnimation.CurrentStyle();
			if (style == FlightStyle.None)
				return null;

			var card = CardAreaOf?.Invoke(scene);
			if (card is not Area cardArea || cardArea.Width < 8 || cardArea.Height < 8)
				return null;

			var monitor = WindowGeometry.BoundsOf(cardArea);
			if (!FlightPlanner.Intersects(monitor, cardArea))
				return null;

			var overlay = new FlightOverlay(monitor, style, SidebarHandle);
			var involved = new List<IntPtr>();
			var priorCardWaits = false;
			var maxVisible = App.Settings.Current.Sidebar.MaxWindowsPerScene;

			try
			{
				overlay.Show();

				// the card of the prior scene takes the place of the card of the new scene, both stacks are laid out on that place
				// exactly like the sidebar draws them, so the flying windows and the card match when one takes over from the other
				var priorWindows = (prior?.Windows ?? Enumerable.Empty<IWindow>()).ToArray();
				var alignment = CardAlignment.For(App.Settings.Current.Sidebar.Side);
				// the card of the scene that leaves may be taller or lower than the one that comes. The stack of the sidebar is centered, so the
				// new card ends up around the same center
				var priorArea = cardArea;
				if (prior is object && CardHeightOf?.Invoke(prior) is double priorHeight && priorHeight > 0)
				{
					var middle = (cardArea.Top + cardArea.Bottom) / 2;
					priorArea = new Area(cardArea.Left, middle - priorHeight / 2, cardArea.Right, middle + priorHeight / 2);
				}

				var priorStack = DragPreviewLayout.OnStack(priorArea, priorWindows.Select(w => WindowGeometry.FrameSizeOf(w.Handle)).ToArray(), maxVisible, CardScale, alignment);

				// the windows that leave fly where they stand to the card, the ones that come fly from the card to where they will show
				for (var i = 0; i < priorWindows.Length; i++)
				{
					var w = priorWindows[i];
					if (!otherWindows.Contains(w))
						continue;

					if (!Win32.IsWindowVisible(w.Handle) || Win32.IsIconic(w.Handle) || Parking.IsParked(w.Handle))
						continue;

					if (WindowGeometry.FrameOf(w.Handle) is not Area frame || !FlightPlanner.Intersects(monitor, frame))
						continue;

					// a window that is not shown on the card fades out on the way
					if (overlay.Add(w.Handle, FlightPlanner.OfFrame(frame), priorStack[i]))
					{
						involved.Add(w.Handle);
						priorCardWaits = true;
					}
				}

				var windows = scene.Windows.ToArray();
				var stack = DragPreviewLayout.OnStack(cardArea, windows.Select(w => WindowGeometry.FrameSizeOf(w.Handle)).ToArray(), maxVisible, CardScale, alignment);

				for (var i = 0; i < windows.Length; i++)
				{
					var w = windows[i];

					// a window that is on screen already (activated from the taskbar or with Alt+Tab) has arrived, it must not fly there once more
					if (Win32.IsWindowVisible(w.Handle) && !Win32.IsIconic(w.Handle) && !Parking.IsParked(w.Handle))
						continue;

					var frame = Parking.OriginalFrameOf(w.Handle) ?? WindowGeometry.FrameOf(w.Handle);
					if (frame is not Area target || !FlightPlanner.Intersects(monitor, target))
						continue;

					// a window that is not shown on the card fades in on the way
					if (overlay.Add(w.Handle, stack[i], FlightPlanner.OfFrame(target)))
						involved.Add(w.Handle);
				}
			}
			catch (Exception ex)
			{
				Log.Error("The animation could not be prepared", ex);
				overlay.Finish();
				return null;
			}

			if (!overlay.HasFlights)
			{
				overlay.Finish();
				return null;
			}

			// the animation Windows plays when a window is shown or minimized would run below ours
			foreach (var h in involved.Union(scene.Windows.Select(w => w.Handle)).Union(otherWindows.Select(w => w.Handle)))
				NativeMethods.SetTransitionsDisabled(h, true);

			_overlay = overlay;
			return new PreparedFlight(overlay, involved, priorCardWaits);
		}

		/// <summary>How long the real windows have to paint themselves under the overlay before it goes. Both show the same picture, so the overlap cannot be seen.</summary>
		private static readonly TimeSpan LandingDelay = TimeSpan.FromMilliseconds(90);

		private async Task RunFlight(PreparedFlight flight, Scene scene, Scene? prior, TaskCompletionSource takeoff)
		{
			var duration = FlightMotion.Duration(App.Settings.Current.Animations.Speed);
			var landed = false;
			var cardLanded = false;

			void Land()
			{
				// the card shows its windows the moment their flying copies arrive on it, both lie exactly on top of each other
				if (!cardLanded && flight.PriorCardWaits && prior is object)
				{
					cardLanded = true;
					CardLanded?.Invoke(this, prior);
				}

				// a newer switch may have taken over while this flight was still in the air: then these windows stay hidden
				if (landed || !ReferenceEquals(_current, scene))
					return;

				landed = true;
				ShowInOrder(scene.Windows);
			}

			try
			{
				_flight = flight.Overlay.Fly(duration);
				takeoff.TrySetResult();

				// never leave the overlay on the screen, whatever happens to the flight
				await Task.WhenAny(_flight, Task.Delay(duration + TimeSpan.FromMilliseconds(1500))).ConfigureAwait(true);

				Land();

				// the real windows and the card are drawn under the overlay before it goes, both show the same picture
				NativeMethods.DwmFlush();
				await Task.Delay(LandingDelay).ConfigureAwait(true);
				NativeMethods.DwmFlush();
			}
			catch (Exception ex)
			{
				Log.Error("The animation failed", ex);
			}
			finally
			{
				// the windows of the scene have to show up even if the animation failed
				Land();

				try
				{
					flight.Overlay.Finish();
				}
				catch (Exception ex)
				{
					Log.Error("The animation overlay could not be closed", ex);
				}

				// only forget the flight if no newer switch has replaced it in the meantime
				if (ReferenceEquals(_overlay, flight.Overlay))
				{
					_overlay = null;
					_flight = Task.CompletedTask;
				}

				// the windows animate on their own again when the user minimizes them
				foreach (var h in GetSceneableWindows().Select(w => w.Handle))
					NativeMethods.SetTransitionsDisabled(h, false);
			}
		}

		private static IReadOnlyList<long> HandlesOf(Scene scene) => scene.Windows.Select(w => w.Handle.ToInt64()).ToList();

		/// <summary>Gives the windows of a scene a new order, back to front. The card follows it.</summary>
		private void Restack(Scene scene, IReadOnlyList<long> backToFront)
		{
			if (scene.Reorder(backToFront.Select(h => new IntPtr(h)).ToList()))
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, scene.Windows.LastOrDefault(), ChangeType.Updated));
		}

		/// <summary>Takes the order in which the windows of a scene lie on the screen now. Windows that are not on the screen keep their place.</summary>
		private void KeepScreenOrder(Scene scene)
		{
			var onScreen = scene.Windows
				.Select(w => w.Handle)
				.Where(h => Win32.IsWindowVisible(h) && !Win32.IsIconic(h) && !Parking.IsParked(h))
				.Select(h => h.ToInt64())
				.ToHashSet();

			if (onScreen.Count < 2)
				return;

			var topToBottom = WindowStack.TopToBottom().Where(onScreen.Contains).ToList();
			Restack(scene, ZOrder.FromScreen(HandlesOf(scene), topToBottom));
		}

		/// <summary>Shows windows and lays them on top of everything else in their order, the last one in front, as the card and the flight showed them.</summary>
		private void ShowInOrder(IEnumerable<IWindow> backToFront)
		{
			var windows = backToFront.ToList();
			var minimized = windows.Where(w => Win32.IsIconic(w.Handle)).Select(w => w.Handle).ToHashSet();

			foreach (var w in windows)
				WindowStrategy.Show(w);

			WindowStack.Restack(windows.Select(w => w.Handle));

			// a snap group whose windows were minimized (they could not be parked) gets its exact layout back
			if (windows.Count > 0 && FindSceneForWindow(windows[0]) is Scene scene && IsSnapGroup(scene))
				RestoreSnapLayout(scene, minimized);
		}

		public Task MoveWindow(Scene sourceScene, IWindow window, Scene targetScene)
		{
			try
			{
				_suspend = true;

				if (sourceScene is null || sourceScene.Equals(targetScene))
					return Task.CompletedTask;

				var moved = _registry.Move(window.Handle.ToInt64(), targetScene.Id);
				if (moved is null)
					return Task.CompletedTask;

				sourceScene.Remove(window);
				targetScene.Add(window);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(targetScene, window, ChangeType.Updated));

				if (moved.Value.SourceRemoved)
				{
					_scenes.Remove(sourceScene);
					_sceneById.Remove(sourceScene.Id);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Removed));
				}
				else
				{
					DissolveIfTooSmall(sourceScene, switchToRemainingWindow: ReferenceEquals(sourceScene, _current));
				}

				if (targetScene.Equals(_current))
				{
					WindowStrategy.Show(window);
					window.Focus();
				}
				else
				{
					// back to the place the window was dragged from, before it is hidden: a hidden window is out of the screen
					if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
						Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y, 0, 0, Win32.SetWindowPosFlags.IgnoreResize);

					WindowStrategy.Hide(window);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || _current is null || sourceScene.Equals(_current))
				return;

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
				await MoveWindow(sourceScene, window, _current).ConfigureAwait(false);
		}

		/// <summary>
		/// Adds all windows and apps of a scene to the scene on the stage. They stay together as one scene from now on.
		/// In the desktop view there is no stage, then the scene is just selected.
		/// </summary>
		public Task JoinToCurrent(Scene source, bool animate = true)
		{
			if (source is null || source.Equals(_current))
				return Task.CompletedTask;

			if (_current is null)
				return SwitchTo(source, animate);

			try
			{
				_suspend = true;

				var target = _current;
				var sourceWasSnapGroup = IsSnapGroup(source);
				if (_registry.Merge(source.Id, target.Id) is null)
					return Task.CompletedTask;

				DebugLog.Write("JOINCARD", $"{DescribeScene(source)} (snap group: {sourceWasSnapGroup}) onto the stage {DescribeScene(target)}, result is a snap group: {IsSnapGroup(target)}");

				var moving = source.Windows.ToList();
				TakeOverLayout(source, target);
				foreach (var w in moving)
				{
					source.Remove(w);
					target.Add(w);
				}

				_scenes.Remove(source);
				_sceneById.Remove(source.Id);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, moving.FirstOrDefault(), ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, moving.FirstOrDefault(), ChangeType.Removed));

				// they come on top of the stage in the order they had on their card, like they flew there. The front one is activated:
				// the user just put them there to work with them, and only the foreground window is sure to stay in front of the others
				ShowInOrder(moving);
				moving.LastOrDefault()?.Focus();

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>
		/// Puts all windows and apps of one card into another card in the sidebar. Neither is on the stage, so no window moves.
		/// Merging into the scene on the stage is <see cref="JoinToCurrent"/>.
		/// </summary>
		public Task MergeScenes(Scene source, Scene target)
		{
			if (source is null || target is null || source.Equals(target))
				return Task.CompletedTask;

			if (target.Equals(_current))
				return JoinToCurrent(source);

			if (source.Equals(_current))
				return Task.CompletedTask;

			try
			{
				_suspend = true;

				var sourceWasSnapGroup = IsSnapGroup(source);
				if (_registry.Merge(source.Id, target.Id) is null)
					return Task.CompletedTask;

				DebugLog.Write("MERGE", $"{DescribeScene(source)} (snap group: {sourceWasSnapGroup}) into {DescribeScene(target)}, result is a snap group: {IsSnapGroup(target)}");

				var moving = source.Windows.ToList();
				TakeOverLayout(source, target);
				foreach (var w in moving)
				{
					source.Remove(w);
					target.Add(w);
				}

				_scenes.Remove(source);
				_sceneById.Remove(source.Id);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, moving.FirstOrDefault(), ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, moving.FirstOrDefault(), ChangeType.Removed));

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>Takes one app out of a scene that holds several apps and gives it a card of its own.</summary>
		public Task SplitApp(Scene scene, string appKey)
		{
			// the windows of an app taken out of a snap group go back to the card of their app, like windows dragged out of the layout
			if (scene is object && IsSnapGroup(scene))
			{
				SplitAppFromSnapGroup(scene, appKey);
				return Task.CompletedTask;
			}

			if (scene is null || _registry.Split(scene.Id, appKey) is not SplitResult split)
				return Task.CompletedTask;

			DebugLog.Write("SPLIT", $"app {appKey} out of {DescribeScene(scene)} into a card of its own");

			try
			{
				_suspend = true;

				var windows = scene.Windows.Where(w => split.Windows.Contains(w.Handle.ToInt64())).ToArray();
				foreach (var w in windows)
					scene.Remove(w);

				var created = new Scene(split.NewGroupId, appKey, windows);
				_scenes.Insert(_scenes.IndexOf(scene) + 1, created);
				_sceneById[created.Id] = created;

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, windows.FirstOrDefault(), ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(created, windows.FirstOrDefault(), ChangeType.Created));

				// a snap group of one window is no layout any more
				DissolveIfTooSmall(scene, switchToRemainingWindow: ReferenceEquals(scene, _current));

				// an app that leaves the stage is no longer shown
				if (scene.Equals(_current))
				{
					foreach (var w in windows)
						WindowStrategy.Hide(w);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		/// <summary>Asks all windows of a scene to close, like the close button of each window does. Apps can still ask to save their work.</summary>
		public void CloseScene(Scene scene)
		{
			if (scene is null)
				return;

			foreach (var w in scene.Windows.ToArray())
				w.Close();
		}

		/// <summary>The apps of a scene that can be excluded: the entry for the list, a name to show and the executable.</summary>
		public static IReadOnlyList<(string Entry, string Name, string? Path)> ExcludableAppsOf(Scene scene) =>
			scene.Windows
				.GroupBy(GroupKeyOf, StringComparer.OrdinalIgnoreCase)
				.Select(g =>
				{
					var first = g.First();
					var path = (first as WindowsWindow)?.ProcessExecutable;
					var entry = ExcludedApps.EntryFor(path);
					return (Entry: string.IsNullOrEmpty(entry) ? first.ProcessFileName : entry, Name: AppInfo.DisplayNameOf(path, first.ProcessName), Path: path);
				})
				.Where(a => !string.IsNullOrEmpty(a.Entry))
				.ToList();

		/// <summary>The apps a scene consists of, with the key that identifies them and a name to show.</summary>
		public static IReadOnlyList<(string Key, string Name)> AppsOf(Scene scene) =>
			scene.Windows
				.GroupBy(GroupKeyOf, StringComparer.OrdinalIgnoreCase)
				.Select(g => (g.Key, g.First().ProcessName))
				.ToList();

		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager?.Windows?.Where(w => !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title));

		public IEnumerable<Scene> GetScenes()
		{
			EnsureScenes();
			return _scenes;
		}

		private void EnsureScenes()
		{
			if (_scenes is not null)
				return;

			var windows = (GetSceneableWindows() ?? Enumerable.Empty<IWindow>()).ToList();
			var byHandle = windows.ToDictionary(w => w.Handle.ToInt64());

			var snapshots = _registry.Initialize(windows.Select(w => (w.Handle.ToInt64(), GroupKeyOf(w))));

			// the windows of each scene lie back to front like they do on the screen, minimized ones included: Windows keeps their place
			var topToBottom = WindowStack.TopToBottom();

			_scenes = new List<Scene>();
			foreach (var group in snapshots)
			{
				var ordered = ZOrder.FromScreen(group.Windows, topToBottom);
				var scene = new Scene(group.Id, group.Key, ordered.Select(h => byHandle[h]).ToArray());
				_scenes.Add(scene);
				_sceneById[group.Id] = scene;
			}
		}

		/// <summary>The windows that decide if the sidebar has to get out of the way. Parked windows are not on the screen, they do not count.</summary>
		public IEnumerable<IWindow> GetCurrentWindows() =>
			(_current?.Windows ?? GetSceneableWindows()).Where(w => !Parking.IsParked(w.Handle));

		// TEMPORARY: logging helper
		private static string DescribeScene(Scene? scene) =>
			scene is null ? "<desktop>" : $"[{string.Join(", ", scene.Windows.Select(w => DebugLog.Describe(w.Handle)))}]";

		public static string GroupKeyOf(IWindow window) =>
			string.IsNullOrEmpty(window.AppKey) ? window.ProcessName.ToUpperInvariant() : window.AppKey;
	}
}
