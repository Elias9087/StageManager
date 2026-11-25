using StageManager.Core;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Strategies
{
	/// <summary>
	/// Hides windows by moving them out of the screen. Unlike minimized windows they keep the picture DWM shows of them,
	/// which Chromium and WebView based windows lose when they are minimized.
	/// A window that cannot be moved (it is minimized already) or the setting "Minimize" fall back to minimizing.
	/// </summary>
	internal class ScreenOffsetWindowStrategy : IWindowStrategy
	{
		private readonly IWindowStrategy _minimize = new NormalizeAndMinimizeWindowStrategy();

		public void Show(IWindow window)
		{
			// whatever way it was hidden, it comes back: parked windows are moved back, minimized ones are restored
			if (Parking.IsParked(window.Handle))
			{
				DebugLog.Write("SHOW", $"unpark {DebugLog.Describe(window.Handle)} before: {DebugLog.State(window.Handle)}");
				Parking.Unpark(window.Handle);

				// the window kept its place in the stack while it was out of the screen, the ones of a scene end up above the others
				Win32.SetWindowPos(window.Handle, Win32.HWND_TOP, 0, 0, 0, 0,
					Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.DoNotActivate);

				// minimized while it was parked: the parker restores it, if it could not (the app hangs) it is restored here
				if (Win32.IsIconic(window.Handle))
					_minimize.Show(window);

				DebugLog.StateNowAndLater("SHOW", "unparked", window.Handle);
				return;
			}

			var minimized = Win32.IsIconic(window.Handle);
			if (minimized)
				DebugLog.Write("SHOW", $"restore {DebugLog.Describe(window.Handle)} before: {DebugLog.State(window.Handle)}");

			_minimize.Show(window);

			if (minimized)
				DebugLog.StateNowAndLater("SHOW", "restored", window.Handle);
		}

		public void Hide(IWindow window)
		{
			// a window its app has hidden (closed to the tray) is not on the screen, it must neither be moved nor minimized
			if (!Win32.IsWindowVisible(window.Handle))
				return;

			// minimized or parked already: nothing changes
			if (Win32.IsIconic(window.Handle) || Parking.IsParked(window.Handle))
				return;

			DebugLog.Write("HIDE", $"{App.Settings.Current.HideStrategy} {DebugLog.Describe(window.Handle)} before: {DebugLog.State(window.Handle)}");

			if (App.Settings.Current.HideStrategy == HideStrategy.Park && Parking.Park(window.Handle))
			{
				DebugLog.StateNowAndLater("HIDE", "parked", window.Handle);
				return;
			}

			_minimize.Hide(window);
			DebugLog.StateNowAndLater("HIDE", "minimized", window.Handle);
		}
	}
}
