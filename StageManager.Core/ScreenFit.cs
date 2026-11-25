namespace StageManager.Core;

/// <summary>
/// Whether a shown window lies well enough on a monitor to be used, and where it goes if not. Pure calculation, no Win32.
/// </summary>
public static class ScreenFit
{
	/// <summary>How much of a window has to lie on one monitor for it to count as on the screen.</summary>
	public const double MinVisibleShare = 0.5;

	/// <summary>
	/// Windows have invisible borders at the left, the right and the bottom. A window that fills the work area sticks out by them,
	/// that is no reason to make it smaller or to move it.
	/// </summary>
	public const double InvisibleBorder = 8;

	/// <summary>True if at least <see cref="MinVisibleShare"/> of the window lies on one monitor. Without monitors nothing can be said.</summary>
	public static bool IsMostlyVisible(Area window, IReadOnlyList<Area> monitors)
	{
		if (monitors.Count == 0 || window.Width <= 0 || window.Height <= 0)
			return true;

		var needed = window.Width * window.Height * MinVisibleShare;
		return monitors.Any(m => Overlap(window, m) >= needed);
	}

	/// <summary>
	/// Moves the window into the work area of the monitor it belongs to, as little as possible. A window larger than the work area
	/// is made smaller. A window that fits stays as it is.
	/// </summary>
	/// <param name="workAreas">One per monitor, in the same order.</param>
	public static Area Fit(Area window, IReadOnlyList<Area> monitors, IReadOnlyList<Area> workAreas)
	{
		if (monitors.Count == 0 || workAreas.Count != monitors.Count)
			return window;

		var work = workAreas[ParkingSpot.NearestMonitor(window, monitors)];
		var area = new Area(work.Left - InvisibleBorder, work.Top, work.Right + InvisibleBorder, work.Bottom + InvisibleBorder);

		var width = Math.Min(window.Width, area.Width);
		var height = Math.Min(window.Height, area.Height);
		var left = Math.Clamp(window.Left, area.Left, area.Right - width);
		var top = Math.Clamp(window.Top, area.Top, area.Bottom - height);

		return new Area(left, top, left + width, top + height);
	}

	/// <summary>True if <paramref name="b"/> has another size than <paramref name="a"/>.</summary>
	public static bool IsResized(Area a, Area b) => Math.Abs(a.Width - b.Width) > 0.5 || Math.Abs(a.Height - b.Height) > 0.5;

	private static double Overlap(Area a, Area b)
	{
		var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
		var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
		return width > 0 && height > 0 ? width * height : 0;
	}
}
