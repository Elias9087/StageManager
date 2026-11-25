using StageManager.Core;

namespace StageManager.Tests;

public class ParkCheckTests
{
	private static readonly Area Main = new(0, 0, 1920, 1080);
	private static readonly Area LeftMonitor = new(-3440, -842, 0, 598);
	private static readonly Area Original = new(-8, -8, 1928, 1160);

	[Fact]
	public void A_window_out_of_the_screen_is_parked()
	{
		var spot = new Area(2080, -8, 4016, 1160);

		Assert.Equal(ParkOutcome.Parked, ParkCheck.Check(Original, spot, spot, new[] { Main, LeftMonitor }, hung: false));
	}

	[Fact]
	public void A_window_parked_on_a_monitor_nobody_knew_of_has_to_be_parked_again()
	{
		// 24.11.: Teams at -2096..-160 stood on the left monitor
		var spot = new Area(-2096, -8, -160, 1160);

		Assert.Equal(ParkOutcome.OnMonitor, ParkCheck.Check(Original, spot, spot, new[] { Main, LeftMonitor }, hung: false));
	}

	[Fact]
	public void A_window_still_where_it_was_was_refused()
	{
		var spot = new Area(2080, -8, 4016, 1160);

		Assert.Equal(ParkOutcome.Refused, ParkCheck.Check(Original, spot, Original, new[] { Main }, hung: false));
	}

	[Fact]
	public void A_window_of_a_hung_app_is_decided_later()
	{
		var spot = new Area(2080, -8, 4016, 1160);

		Assert.Equal(ParkOutcome.Parked, ParkCheck.Check(Original, spot, Original, new[] { Main }, hung: true));
	}

	[Fact]
	public void A_parked_window_needs_nothing_more()
	{
		Assert.Equal(ParkFollowUp.None, ParkCheck.FollowUp(ParkOutcome.Parked, parkedBefore: false, monitorsChangedLately: false, refusedBefore: false));
	}

	[Fact]
	public void A_window_on_a_monitor_is_parked_again()
	{
		Assert.Equal(ParkFollowUp.Repark, ParkCheck.FollowUp(ParkOutcome.OnMonitor, parkedBefore: false, monitorsChangedLately: false, refusedBefore: true));
	}

	[Fact]
	public void A_first_refusal_is_checked_once_more()
	{
		Assert.Equal(ParkFollowUp.Repark, ParkCheck.FollowUp(ParkOutcome.Refused, parkedBefore: false, monitorsChangedLately: false, refusedBefore: false));
	}

	[Fact]
	public void A_window_that_never_moved_and_refused_twice_is_given_up()
	{
		Assert.Equal(ParkFollowUp.Abandon, ParkCheck.FollowUp(ParkOutcome.Refused, parkedBefore: false, monitorsChangedLately: false, refusedBefore: true));
	}

	[Fact]
	public void A_window_that_was_parked_before_is_never_given_up()
	{
		// 25.11. 12:07:22: "Einstellungen" was parked at 12:07:19 and counted as refused while the monitors changed
		Assert.Equal(ParkFollowUp.Repark, ParkCheck.FollowUp(ParkOutcome.Refused, parkedBefore: true, monitorsChangedLately: false, refusedBefore: true));
	}

	[Fact]
	public void No_window_is_given_up_right_after_the_monitors_changed()
	{
		Assert.Equal(ParkFollowUp.Repark, ParkCheck.FollowUp(ParkOutcome.Refused, parkedBefore: false, monitorsChangedLately: true, refusedBefore: true));
	}
}
