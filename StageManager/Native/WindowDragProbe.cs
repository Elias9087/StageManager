using StageManager.Native.PInvoke;
using System;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>Reads what a window drag started with: the part of the window under the pointer and the mouse button.</summary>
	public static class WindowDragProbe
	{
		private const uint WM_NCHITTEST = 0x0084;
		private const uint SMTO_ABORTIFHUNG = 0x0002;
		private const uint HitTestTimeoutMs = 100;
		private const int VK_LBUTTON = 0x01;
		private const int VK_RBUTTON = 0x02;
		private const int SM_SWAPBUTTON = 23;

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

		[DllImport("user32.dll")]
		private static extern int GetSystemMetrics(int index);

		[DllImport("user32.dll")]
		private static extern uint GetDpiForWindow(IntPtr hWnd);

		/// <summary>
		/// Asks the window what is at a point on the screen (WM_NCHITTEST). Null if it does not answer in time: the app is busy or hung.
		/// </summary>
		public static int? HitTest(IntPtr window, int x, int y)
		{
			// screen coordinates as two signed 16 bit values, like Windows sends them
			var lParam = new IntPtr((int)(((uint)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF)));
			if (SendMessageTimeout(window, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, HitTestTimeoutMs, out var result) == IntPtr.Zero)
				return null;

			return unchecked((int)result.ToInt64());
		}

		/// <summary>The primary mouse button is pressed. With swapped buttons that is the right one, GetAsyncKeyState reads the physical buttons.</summary>
		public static bool IsPrimaryButtonDown()
		{
			var vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
			return (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
		}

		/// <summary>Physical pixels per device independent unit of the monitor the window is on.</summary>
		public static double ScaleOf(IntPtr window)
		{
			var dpi = GetDpiForWindow(window);
			return dpi == 0 ? 1 : dpi / 96.0;
		}
	}
}
