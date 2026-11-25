using StageManager.Core;

namespace StageManager.Tests;

public class ScreenFitTests
{
	private static readonly Area Laptop = new(0, 0, 1920, 1200);
	private static readonly Area LaptopWork = new(0, 0, 1920, 1152);
	private static readonly IReadOnlyList<Area> Monitors = new[] { Laptop };
	private static readonly IReadOnlyList<Area> WorkAreas = new[] { LaptopWork };

	[Fact]
	public void Half_of_a_window_on_a_monitor_is_enough()
	{
		Assert.True(ScreenFit.IsMostlyVisible(new Area(-400, 0, 400, 600), Monitors));
		Assert.False(ScreenFit.IsMostlyVisible(new Area(-401, 0, 399, 600), Monitors));
	}

	[Fact]
	public void Teams_after_the_monitor_was_unplugged_is_not_on_the_screen()
	{
		// 25.11. 12:02:48: maximized at (-1696,259,240,1427) on the laptop
		Assert.False(ScreenFit.IsMostlyVisible(new Area(-1696, 259, 240, 1427), Monitors));
	}

	[Fact]
	public void A_maximized_window_with_its_invisible_borders_counts_as_on_the_screen()
	{
		Assert.True(ScreenFit.IsMostlyVisible(new Area(-8, -8, 1928, 1160), Monitors));
	}

	[Fact]
	public void Without_monitors_nothing_is_out_of_the_screen()
	{
		Assert.True(ScreenFit.IsMostlyVisible(new Area(-5000, 0, -4000, 600), Array.Empty<Area>()));
	}

	[Fact]
	public void A_window_that_fits_is_moved_into_the_work_area_and_keeps_its_size()
	{
		var fitted = ScreenFit.Fit(new Area(1746, 270, 2421, 695), Monitors, WorkAreas);

		Assert.Equal(new Area(1253, 270, 1928, 695), fitted);
	}

	[Fact]
	public void A_snapped_window_that_fills_the_height_is_not_made_smaller()
	{
		// the invisible border at the bottom sticks out of the work area
		var snapped = new Area(-1894, 0, -160, 1159);

		var fitted = ScreenFit.Fit(snapped, Monitors, WorkAreas);

		Assert.False(ScreenFit.IsResized(snapped, fitted));
		Assert.Equal(-8, fitted.Left);
	}

	[Fact]
	public void A_window_larger_than_the_work_area_is_made_smaller()
	{
		var fitted = ScreenFit.Fit(new Area(2000, 100, 4800, 1500), Monitors, WorkAreas);

		Assert.Equal(new Area(-8, 0, 1928, 1160), fitted);
	}

	[Fact]
	public void The_window_goes_to_the_monitor_it_overlaps_most()
	{
		var right = new Area(1920, 0, 3840, 1080);
		var fitted = ScreenFit.Fit(new Area(3700, 100, 4300, 500), new[] { Laptop, right }, new[] { LaptopWork, right });

		Assert.Equal(new Area(3248, 100, 3848, 500), fitted);
	}
}
