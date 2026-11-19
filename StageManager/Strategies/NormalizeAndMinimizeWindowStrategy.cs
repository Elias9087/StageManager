using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Strategies
{
    internal class NormalizeAndMinimizeWindowStrategy : IWindowStrategy
	{
		public void Show(IWindow window)
		{
			// a window its app has hidden (closed to the tray) must not come back as a "zombie"
			if (!Win32.IsWindowVisible(window.Handle))
				return;

			// a window on the screen stays untouched: SW_SHOWNOACTIVATE would restore a snapped or maximized window
			// to its normal size and break the snap group of Windows
			if (!Win32.IsIconic(window.Handle))
				return;

			Win32.ShowWindow(window.Handle, Win32.SW.SW_SHOWNOACTIVATE);
		}

		public void Hide(IWindow window)
		{
			// SW_MINIMIZE would show a hidden window again, minimized in the taskbar
			if (!Win32.IsWindowVisible(window.Handle) || Win32.IsIconic(window.Handle))
				return;

			Win32.ShowWindow(window.Handle, Win32.SW.SW_MINIMIZE);
		}
	}
}
