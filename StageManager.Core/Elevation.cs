namespace StageManager.Core;

/// <summary>
/// Windows of apps that run with more rights than StageManager (e.g. as administrator) cannot be moved, minimized or activated by it
/// (User Interface Privilege Isolation). Such windows are not managed: they would stay on the screen over every other scene.
/// </summary>
public static class Elevation
{
	/// <summary>True if StageManager can move the windows of a process of the given integrity level.</summary>
	/// <param name="processLevel">The integrity level of the process, null if it could not be read: then it most likely runs with more rights.</param>
	/// <param name="ownLevel">The integrity level of StageManager, null if unknown: then nothing is excluded.</param>
	public static bool CanManage(int? processLevel, int? ownLevel)
	{
		if (ownLevel is not int own)
			return true;

		return processLevel is int level && level <= own;
	}

	/// <summary>
	/// True if Windows refused to park a window: right after it was parked it is not at its parking spot but still where it was.
	/// A window of an app that hangs is not counted, it moves once the app responds again.
	/// </summary>
	/// <param name="original">Where the window was before it was parked (the window rectangle, maximized or not).</param>
	/// <param name="parked">The parking spot it was sent to.</param>
	/// <param name="now">Where the window is now.</param>
	public static bool RefusedToMove(Area original, Area parked, Area now, bool hung, double tolerance = 2) =>
		!hung && !SnapGeometry.IsSame(parked, now, tolerance) && SnapGeometry.IsSame(original, now, tolerance);
}
