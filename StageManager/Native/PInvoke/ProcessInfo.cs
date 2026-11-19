using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StageManager.Native.PInvoke
{
	/// <summary>
	/// Finds out which executable a window belongs to. Does not use Process.MainModule, which is slow and fails
	/// with "Access denied" for processes that run with elevated rights.
	/// </summary>
	public static class ProcessInfo
	{
		private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		private const uint SMTO_ABORTIFHUNG = 0x0002;
		private const uint WM_GETICON = 0x007F;
		private const int ICON_SMALL = 0;
		private const int ICON_BIG = 1;
		private const int ICON_SMALL2 = 2;
		private const int GCLP_HICON = -14;

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

		[DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
		private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int index);

		[DllImport("user32.dll", EntryPoint = "GetClassLongW")]
		private static extern uint GetClassLong32(IntPtr hWnd, int index);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);

		[DllImport("user32.dll")]
		private static extern bool IsHungAppWindow(IntPtr hWnd);

		/// <returns>The full path of the executable, or null if it cannot be read.</returns>
		public static string? GetExecutablePath(uint processId)
		{
			var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
			if (process == IntPtr.Zero)
				return null;

			try
			{
				var buffer = new StringBuilder(1024);
				var size = (uint)buffer.Capacity;
				return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : null;
			}
			finally
			{
				CloseHandle(process);
			}
		}

		/// <summary>
		/// The process a window really belongs to. The window of a Store app is owned by the ApplicationFrameHost,
		/// the app itself lives in the process of the CoreWindow inside of it.
		/// </summary>
		public static uint GetOwningProcessId(IntPtr window, string className)
		{
			Win32.GetWindowThreadProcessId(window, out var processId);

			if (className == "ApplicationFrameWindow")
			{
				var core = FindWindowEx(window, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
				if (core != IntPtr.Zero)
				{
					Win32.GetWindowThreadProcessId(core, out var appProcessId);
					if (appProcessId != 0)
						return appProcessId;
				}
			}

			return processId;
		}

		public static bool IsHung(IntPtr window) => IsHungAppWindow(window);

		private const uint TOKEN_QUERY = 0x0008;
		private const int TokenIntegrityLevel = 25;

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

		[DllImport("advapi32.dll")]
		private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

		[DllImport("advapi32.dll")]
		private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

		/// <summary>
		/// The integrity level of a process (e.g. 0x2000 medium, 0x3000 high for an app that runs as administrator), null if it cannot
		/// be read. A process of a higher level does not let itself be moved or minimized (UIPI).
		/// </summary>
		public static int? IntegrityLevelOf(uint processId)
		{
			var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
			if (process == IntPtr.Zero)
				return null;

			try
			{
				if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
					return null;

				try
				{
					GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
					if (length <= 0)
						return null;

					var buffer = Marshal.AllocHGlobal(length);
					try
					{
						if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
							return null;

						// TOKEN_MANDATORY_LABEL starts with SID_AND_ATTRIBUTES, whose first member is the SID pointer
						var sid = Marshal.ReadIntPtr(buffer);
						var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
						return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
					}
					finally
					{
						Marshal.FreeHGlobal(buffer);
					}
				}
				finally
				{
					CloseHandle(token);
				}
			}
			finally
			{
				CloseHandle(process);
			}
		}

		/// <summary>
		/// Asks the window for its icon. Gives up after a short time instead of blocking on an app that does not respond.
		/// </summary>
		public static IntPtr GetWindowIcon(IntPtr window)
		{
			foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
			{
				if (SendMessageTimeout(window, WM_GETICON, new IntPtr(kind), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var icon) != IntPtr.Zero && icon != IntPtr.Zero)
					return icon;
			}

			var fromClass = IntPtr.Size == 8 ? GetClassLongPtr64(window, GCLP_HICON) : new IntPtr(unchecked((int)GetClassLong32(window, GCLP_HICON)));
			return fromClass;
		}
	}
}
