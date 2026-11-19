using StageManager.Core;

namespace StageManager.Tests;

public class ElevationTests
{
	private const int Medium = 0x2000;
	private const int High = 0x3000;

	[Theory]
	[InlineData(Medium, Medium, true)]
	[InlineData(0x1000, Medium, true)]
	[InlineData(High, Medium, false)]
	[InlineData(High, High, true)]
	[InlineData(0x4000, High, false)]
	public void Only_apps_without_more_rights_are_managed(int processLevel, int ownLevel, bool expected)
	{
		Assert.Equal(expected, Elevation.CanManage(processLevel, ownLevel));
	}

	[Fact]
	public void An_app_whose_level_cannot_be_read_most_likely_runs_with_more_rights()
	{
		Assert.False(Elevation.CanManage(null, Medium));
	}

	[Fact]
	public void Without_an_own_level_nothing_is_excluded()
	{
		Assert.True(Elevation.CanManage(null, null));
		Assert.True(Elevation.CanManage(High, null));
	}

	private static readonly Area Original = new(260, 105, 1558, 1033);
	private static readonly Area Spot = new(-1458, 105, -160, 1033);

	[Fact]
	public void A_parked_window_still_at_its_place_was_refused()
	{
		// VMware as administrator: the rectangle did not change
		Assert.True(Elevation.RefusedToMove(Original, Spot, Original, hung: false));
	}

	[Fact]
	public void A_parked_window_at_its_parking_spot_was_not_refused()
	{
		Assert.False(Elevation.RefusedToMove(Original, Spot, Spot, hung: false));
	}

	[Fact]
	public void A_maximized_window_parked_as_it_is_was_not_refused()
	{
		// Zen maximized: (-8,-8,1928,1160) was moved to (-2096,-8,-160,1160) and stays maximized
		var maximized = new Area(-8, -8, 1928, 1160);
		var spot = new Area(-2096, -8, -160, 1160);

		Assert.False(Elevation.RefusedToMove(maximized, spot, spot, hung: false));
	}

	[Fact]
	public void A_window_somewhere_else_was_moved_by_someone_and_not_refused()
	{
		Assert.False(Elevation.RefusedToMove(Original, Spot, new Area(500, 300, 1798, 1228), hung: false));
	}

	[Fact]
	public void A_window_of_a_hung_app_is_not_refused_it_moves_later()
	{
		Assert.False(Elevation.RefusedToMove(Original, Spot, Original, hung: true));
	}
}
