using StageManager.Core;

namespace StageManager.Tests;

public class SnapGeometryTests
{
	// two halves of a 1920x1152 screen as Windows snaps them, the visible frames touch at x = 960
	private static readonly Area Left = new(0, 0, 960, 1152);
	private static readonly Area Right = new(960, 0, 1920, 1152);
	private static readonly Area TopRight = new(960, 0, 1920, 576);
	private static readonly Area BottomRight = new(960, 576, 1920, 1152);

	[Fact]
	public void Two_halves_are_adjacent()
	{
		Assert.True(SnapGeometry.AreAdjacent(Left, Right));
		Assert.True(SnapGeometry.AreAdjacent(Right, Left));
	}

	[Fact]
	public void Quarters_above_each_other_are_adjacent()
	{
		Assert.True(SnapGeometry.AreAdjacent(TopRight, BottomRight));
	}

	[Fact]
	public void A_small_gap_within_the_tolerance_still_counts()
	{
		Assert.True(SnapGeometry.AreAdjacent(Left, new Area(970, 0, 1920, 1152)));
		Assert.False(SnapGeometry.AreAdjacent(Left, new Area(980, 0, 1920, 1152)));
	}

	[Fact]
	public void Rectangles_touching_only_at_a_corner_are_not_adjacent()
	{
		Assert.False(SnapGeometry.AreAdjacent(new Area(0, 0, 960, 576), BottomRight));
	}

	[Fact]
	public void Overlapping_windows_are_not_adjacent_but_overlap()
	{
		var middle = new Area(400, 200, 1400, 900);

		Assert.False(SnapGeometry.AreAdjacent(Left, middle));
		Assert.True(SnapGeometry.OverlapsSignificantly(Left, middle));
	}

	[Fact]
	public void Neighbours_do_not_overlap_significantly()
	{
		Assert.False(SnapGeometry.OverlapsSignificantly(Left, new Area(955, 0, 1920, 1152)));
	}

	[Fact]
	public void A_stored_window_rectangle_with_invisible_borders_does_not_overlap_its_neighbour_frame()
	{
		// GetWindowRect includes 7 invisible pixels at the sides
		var storedRight = new Area(953, 0, 1927, 1159);

		Assert.False(SnapGeometry.LayoutOverlaps(storedRight, Left));
		Assert.True(SnapGeometry.LayoutOverlaps(storedRight, TopRight));
	}

	[Fact]
	public void Connected_follows_chains_of_touching_windows()
	{
		var frames = new Dictionary<string, Area>
		{
			["left"] = Left,
			["topRight"] = TopRight,
			["bottomRight"] = BottomRight,
			["far"] = new Area(3000, 0, 3500, 500)
		};

		var connected = SnapGeometry.Connected("bottomRight", frames.Keys, k => frames[k]);

		Assert.Equal("bottomRight", connected[0]);
		Assert.Equal(new[] { "bottomRight", "left", "topRight" }.OrderBy(x => x), connected.OrderBy(x => x));
	}

	[Fact]
	public void Connected_of_a_lonely_window_is_only_itself()
	{
		var connected = SnapGeometry.Connected(1, new[] { 1, 2 }, i => i == 1 ? Left : new Area(3000, 0, 3500, 500));

		Assert.Equal(new[] { 1 }, connected);
	}

	[Fact]
	public void Dragging_moves_all_four_edges()
	{
		Assert.True(SnapGeometry.WasDragged(Left, new Area(100, 50, 1060, 1202)));
	}

	[Theory]
	[InlineData(0, 0, 1100, 1152)]    // right border
	[InlineData(0, 100, 1100, 1152)]  // top right corner
	[InlineData(0, 0, 800, 1152)]     // the shared border moved to the left
	public void Resizing_is_not_dragging(double left, double top, double right, double bottom)
	{
		Assert.False(SnapGeometry.WasDragged(Left, new Area(left, top, right, bottom)));
	}

	[Fact]
	public void A_tiny_move_within_the_tolerance_is_not_dragging()
	{
		Assert.False(SnapGeometry.WasDragged(Left, new Area(1, 1, 961, 1153)));
		Assert.True(SnapGeometry.IsSame(Left, new Area(1, 1, 961, 1153)));
		Assert.False(SnapGeometry.IsSame(Left, new Area(4, 0, 960, 1152)));
	}
}
