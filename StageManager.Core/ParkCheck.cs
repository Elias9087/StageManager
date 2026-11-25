namespace StageManager.Core;

public enum ParkOutcome
{
	/// <summary>The window is out of the screen, or its app hangs and it moves later.</summary>
	Parked,

	/// <summary>Windows refused to move the window: it is still where it was. Its app runs with more rights than StageManager.</summary>
	Refused,

	/// <summary>The window moved but is on a monitor, e.g. one StageManager did not know of when it chose the parking spot. Park it again.</summary>
	OnMonitor
}

/// <summary>What happens to a window after the check.</summary>
public enum ParkFollowUp
{
	/// <summary>It is out of the screen, nothing to do.</summary>
	None,

	/// <summary>It is parked once more and, after a refusal, checked again.</summary>
	Repark,

	/// <summary>It is not managed any more: Windows does not let StageManager move it.</summary>
	Abandon
}

/// <summary>Checks shortly after a window was parked whether it really left the screen.</summary>
public static class ParkCheck
{
	/// <summary>
	/// How long after a change of the monitors windows are not given up. Windows moves windows itself then, e.g. back to where they
	/// were the last time these monitors were connected, which looks like a refusal.
	/// </summary>
	public static readonly TimeSpan MonitorSettleTime = TimeSpan.FromSeconds(5);

	/// <param name="original">Where the window was before it was parked.</param>
	/// <param name="spot">The parking spot it was sent to.</param>
	/// <param name="now">Where the window is now.</param>
	/// <param name="monitors">The monitors as they are now.</param>
	public static ParkOutcome Check(Area original, Area spot, Area now, IReadOnlyList<Area> monitors, bool hung)
	{
		if (hung || !monitors.Any(m => FlightPlanner.Intersects(m, now)))
			return ParkOutcome.Parked;

		return Elevation.RefusedToMove(original, spot, now, hung) ? ParkOutcome.Refused : ParkOutcome.OnMonitor;
	}

	/// <summary>
	/// Only a window that really never moves is given up: it was never parked successfully, the monitors did not change a moment ago,
	/// and it refused already once before (an app that is busy for a moment moves late).
	/// </summary>
	/// <param name="parkedBefore">The window left the screen at an earlier check: Windows does let StageManager move it.</param>
	/// <param name="monitorsChangedLately">The monitors changed less than <see cref="MonitorSettleTime"/> ago.</param>
	/// <param name="refusedBefore">The check right before this one found it refused already and parked it once more.</param>
	public static ParkFollowUp FollowUp(ParkOutcome outcome, bool parkedBefore, bool monitorsChangedLately, bool refusedBefore) => outcome switch
	{
		ParkOutcome.Parked => ParkFollowUp.None,
		ParkOutcome.OnMonitor => ParkFollowUp.Repark,
		_ => parkedBefore || monitorsChangedLately || !refusedBefore ? ParkFollowUp.Repark : ParkFollowUp.Abandon
	};
}
