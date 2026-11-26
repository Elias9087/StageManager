using StageManager.Core;
using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace StageManager.Native
{
	/// <summary>Moves windows with Win32 for <see cref="WindowParker"/>.</summary>
	public sealed class Win32WindowPlacer : IWindowPlacer
	{
		private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

		public bool Exists(long window, int processId)
		{
			var handle = new IntPtr(window);
			return Win32.IsWindow(handle) && ProcessIdOf(window) == processId;
		}

		public int ProcessIdOf(long window)
		{
			Win32.GetWindowThreadProcessId(new IntPtr(window), out var processId);
			return (int)processId;
		}

		public Area? GetRect(long window)
		{
			var rect = new Win32.Rect();
			return Win32.GetWindowRect(new IntPtr(window), ref rect) ? new Area(rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
		}

		public Area? GetFrame(long window)
		{
			if (Win32.DwmGetWindowAttribute(new IntPtr(window), DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.Rect frame, Marshal.SizeOf<Win32.Rect>()) == 0
				&& frame.Right > frame.Left && frame.Bottom > frame.Top)
				return new Area(frame.Left, frame.Top, frame.Right, frame.Bottom);

			return GetRect(window);
		}

		public bool IsMinimized(long window) => Win32.IsIconic(new IntPtr(window));

		public bool IsMaximized(long window) => Win32.IsZoomed(new IntPtr(window));

		public Area? GetRestoredRect(long window) =>
			Interop.NativeMethods.TryGetNormalPosition(new IntPtr(window), out var r) ? new Area(r.left, r.top, r.right, r.bottom) : null;

		public bool Normalize(long window, Area rect)
		{
			if (ProcessInfo.IsHung(new IntPtr(window)))
				return false;
			return Interop.NativeMethods.SetPlacement(new IntPtr(window), ToWorkspace(rect), maximized: false);
		}

		public bool Place(long window, Area rect, bool maximized)
		{
			if (ProcessInfo.IsHung(new IntPtr(window)))
				return false;

			// a maximized window is restored onto the monitor of the rectangle first: Windows maximizes it on the monitor it is on
			var normal = ToWorkspace(rect);
			if (!Interop.NativeMethods.SetPlacement(new IntPtr(window), normal, maximized: false))
				return false;

			return !maximized || Interop.NativeMethods.SetPlacement(new IntPtr(window), normal, maximized: true);
		}

		/// <summary>
		/// The placement is in workspace coordinates, they start at the work area of the monitor instead of the monitor. The monitors
		/// are read fresh: the ones of Windows Forms are cached and may be outdated right after a monitor was plugged in or out.
		/// </summary>
		private static Interop.RECT ToWorkspace(Area rect)
		{
			var monitors = StageManager.Native.Monitors.All();
			var dx = 0.0;
			var dy = 0.0;

			if (monitors.Count > 0)
			{
				var monitor = monitors[ParkingSpot.NearestMonitor(rect, monitors.Select(m => m.Bounds).ToList())];
				dx = monitor.WorkArea.Left - monitor.Bounds.Left;
				dy = monitor.WorkArea.Top - monitor.Bounds.Top;
			}

			return new Interop.RECT { left = (int)(rect.Left - dx), top = (int)(rect.Top - dy), right = (int)(rect.Right - dx), bottom = (int)(rect.Bottom - dy) };
		}

		public bool Maximize(long window, Area restored)
		{
			// the placement is set synchronously, an app that hangs would block StageManager. Its window comes back unmaximized then
			if (ProcessInfo.IsHung(new IntPtr(window)))
				return false;

			return Interop.NativeMethods.SetPlacement(new IntPtr(window),
				new Interop.RECT { left = (int)restored.Left, top = (int)restored.Top, right = (int)restored.Right, bottom = (int)restored.Bottom },
				maximized: true);
		}

		/// <summary>
		/// Asynchronous, so an app that hangs cannot block StageManager. The size stays, the window keeps its z-order and is not activated.
		/// </summary>
		public bool Move(long window, Area rect, bool refreshFrame)
		{
			var flags = Win32.SetWindowPosFlags.IgnoreResize
				| Win32.SetWindowPosFlags.IgnoreZOrder
				| Win32.SetWindowPosFlags.DoNotActivate
				| Win32.SetWindowPosFlags.DoNotChangeOwnerZOrder
				| Win32.SetWindowPosFlags.AsynchronousWindowPosition;

			// Chromium windows lose their rounded corners if their frame is not recalculated after they came back
			if (refreshFrame)
				flags |= Win32.SetWindowPosFlags.FrameChanged;

			return Win32.SetWindowPos(new IntPtr(window), IntPtr.Zero, (int)rect.Left, (int)rect.Top, 0, 0, flags);
		}

		public IReadOnlyList<long> OwnedWindows(long window)
		{
			var owned = new List<long>();
			var owner = new IntPtr(window);

			Win32.EnumWindows((handle, _) =>
			{
				if (Win32.GetWindow(handle, Win32.GW.GW_OWNER) == owner && Win32.IsWindowVisible(handle))
					owned.Add(handle.ToInt64());

				return true;
			}, IntPtr.Zero);

			return owned;
		}

		public IReadOnlyList<Area> Monitors() => StageManager.Native.Monitors.Bounds();

		public IReadOnlyList<Area> WorkAreas() => StageManager.Native.Monitors.All().Select(m => m.WorkArea).ToList();
	}

	/// <summary>The one parker of the app. Windows hidden by moving them out of the screen are listed in a journal on disk.</summary>
	public static class Parking
	{
		private static readonly Lazy<WindowParker> Instance = new(() =>
			// maximized windows are moved as they are: maximizing them again when they come back makes Chromium based windows black for a moment
			new WindowParker(new Win32WindowPlacer(), new FileParkingJournal(Path.Combine(AppPaths.DataDirectory, "parked-windows.json")), MaximizedParking.MoveMaximized));

		public static bool IsParked(IntPtr window) => Instance.Value.IsParked(window.ToInt64());

		public static bool Park(IntPtr window) => Instance.Value.Park(window.ToInt64());

		public static bool Unpark(IntPtr window) => Instance.Value.Unpark(window.ToInt64());

		public static void Forget(IntPtr window) => Instance.Value.Forget(window.ToInt64());

		public static Area? OriginalFrameOf(IntPtr window) => Instance.Value.OriginalFrameOf(window.ToInt64());

		public static Area? OriginalRectOf(IntPtr window) => Instance.Value.OriginalRectOf(window.ToInt64());

		public static Area? ParkedRectOf(IntPtr window) => Instance.Value.ParkedRectOf(window.ToInt64());

		/// <summary>Changes where a parked window comes back to, see <see cref="WindowParker.Retarget"/>.</summary>
		public static bool Retarget(IntPtr window, Area original) => Instance.Value.Retarget(window.ToInt64(), original);

		/// <summary>Parks the windows again that the changed monitors put on the screen, see <see cref="WindowParker.Repark()"/>.</summary>
		public static int Repark()
		{
			var count = Instance.Value.Repark();
			if (count > 0)
				Log.Info($"{count} parked window(s) parked again after the monitors changed");

			return count;
		}

		public static bool Repark(IntPtr window) => Instance.Value.Repark(window.ToInt64());

		/// <summary>Puts a shown window back onto a monitor if most of it is out of the screen, see <see cref="WindowParker.EnsureOnScreen"/>.</summary>
		public static bool EnsureOnScreen(IntPtr window) => Instance.Value.EnsureOnScreen(window.ToInt64());

		public static int RestoreAll()
		{
			// nothing was parked if the parker was never created, but a journal of a killed run may still exist
			var restored = Instance.Value.RestoreAll();
			if (restored > 0)
				Log.Info($"{restored} parked window(s) brought back");

			return restored;
		}

		public static int Recover()
		{
			var restored = Instance.Value.Recover();
			if (restored > 0)
				Log.Warn($"{restored} window(s) left out of the screen by a previous run were brought back");

			return restored;
		}
	}
}
