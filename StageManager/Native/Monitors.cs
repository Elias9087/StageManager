using StageManager.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>
	/// The monitors as Windows knows them right now. Asked fresh every time: Screen.AllScreens of Windows Forms keeps a copy that can lag
	/// behind when a monitor is plugged in or out, then windows were parked on a monitor nobody knew about.
	/// </summary>
	public static class Monitors
	{
		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int Left, Top, Right, Bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MONITORINFO
		{
			public int cbSize;
			public RECT rcMonitor;
			public RECT rcWork;
			public uint dwFlags;
		}

		private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

		[DllImport("user32.dll")]
		private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

		/// <summary>Bounds and work area of every monitor, in the same order.</summary>
		public static IReadOnlyList<(Area Bounds, Area WorkArea)> All()
		{
			var result = new List<(Area, Area)>();

			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
			{
				var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
				if (GetMonitorInfo(monitor, ref info))
					result.Add((ToArea(info.rcMonitor), ToArea(info.rcWork)));

				return true;
			}, IntPtr.Zero);

			return result;
		}

		public static IReadOnlyList<Area> Bounds() => All().Select(m => m.Bounds).ToList();

		/// <summary>True if the rectangle overlaps any monitor.</summary>
		public static bool AnyIntersects(Area rect) => All().Any(m => FlightPlanner.Intersects(m.Bounds, rect));

		private static Area ToArea(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
	}
}
