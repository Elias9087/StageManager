using StageManager.Native.PInvoke;
using System;
using System.Diagnostics;
using System.Text;

namespace StageManager
{
	/// <summary>
	/// TEMPORARY: detailed logging to analyse hidden windows and snap groups. Writes into the normal log (debug.log).
	/// Can be switched off with <see cref="Enabled"/> or removed completely once the features work.
	/// </summary>
	internal static class DebugLog
	{
		public static bool Enabled { get; set; } = true;

		public static void Write(string category, string message)
		{
			if (Enabled)
				Log.Debug($"[{category}] {message}");
		}

		/// <summary>Handle, process and title of a window, e.g. 0x1A2B msedge "Neuer Tab".</summary>
		public static string Describe(IntPtr handle)
		{
			try
			{
				var title = new StringBuilder(256);
				Win32.GetWindowText(handle, title, title.Capacity);

				Win32.GetWindowThreadProcessId(handle, out var processId);
				var process = "?";
				try
				{
					using var p = Process.GetProcessById((int)processId);
					process = p.ProcessName;
				}
				catch
				{
					// the process may be gone already
				}

				return $"0x{handle.ToInt64():X} {process} \"{title}\"";
			}
			catch
			{
				return $"0x{handle.ToInt64():X}";
			}
		}

		/// <summary>Visibility, minimized and maximized state and the window rectangle.</summary>
		public static string State(IntPtr handle)
		{
			var rect = new Win32.Rect();
			Win32.GetWindowRect(handle, ref rect);

			return $"arranged={IsArranged(handle)} exists={Win32.IsWindow(handle)} visible={Win32.IsWindowVisible(handle)} minimized={Win32.IsIconic(handle)} maximized={Win32.IsZoomed(handle)} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})";
		}

		private static string IsArranged(IntPtr handle)
		{
			try
			{
				return Win32.IsWindowArranged(handle) ? "True" : "False";
			}
			catch (EntryPointNotFoundException)
			{
				return "n/a";
			}
		}

		/// <summary>Writes the state of a window now and once more after a delay, e.g. after an asynchronous move has arrived.</summary>
		public static void StateNowAndLater(string category, string what, IntPtr handle, int delayMs = 500)
		{
			if (!Enabled)
				return;

			Write(category, $"{what} {Describe(handle)} {State(handle)}");

			System.Threading.Tasks.Task.Run(async () =>
			{
				await System.Threading.Tasks.Task.Delay(delayMs).ConfigureAwait(false);
				Write(category, $"{what} +{delayMs} ms {Describe(handle)} {State(handle)}");
			});
		}
	}
}
