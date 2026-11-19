using StageManager.Core;

namespace StageManager.Tests;

public class IconRowTests
{
	private const double CardWidth = 200;
	private const double CardHeight = 125;

	[Fact]
	public void A_wide_window_fills_the_card_and_so_does_the_row()
	{
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (1600.0, 1000.0) }, 3, alignX: 0, icons: 1);

		Assert.Equal(CardWidth, width, 3);
	}

	[Fact]
	public void A_narrow_window_on_the_left_ends_the_row_at_its_right_edge()
	{
		// 300 x 1000 fitted into 200 x 125: 37.5 wide, the row needs 31 + 26 = 57 for one icon and the button
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (300.0, 1000.0) }, 3, alignX: 0, icons: 1);

		Assert.Equal(57, width, 3);
	}

	[Fact]
	public void A_narrow_but_not_tiny_window_decides_the_width()
	{
		// 600 x 1000 fitted: 75 wide, more than the icons need
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (600.0, 1000.0) }, 3, alignX: 0, icons: 1);

		Assert.Equal(75, width, 3);
	}

	[Fact]
	public void On_the_right_side_the_row_is_measured_from_the_right_edge()
	{
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (600.0, 1000.0) }, 3, alignX: 1, icons: 1);

		Assert.Equal(75, width, 3);
	}

	[Fact]
	public void A_stack_reaches_as_far_as_its_widest_window_with_its_offset()
	{
		// back: wide window, front: narrow one. Both slots are 192 x 117, the one in the back is offset by one step of 8.
		// The wide window fits its slot by height: 1600 * 117 / 1000 = 187.2 wide, so it reaches to 8 + 187.2
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (1600.0, 1000.0), (300.0, 1000.0) }, 3, alignX: 0, icons: 2);

		Assert.Equal(195.2, width, 3);
	}

	[Fact]
	public void Hidden_windows_beyond_the_visible_stack_do_not_count()
	{
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (1600.0, 1000.0), (600.0, 1000.0) }, 1, alignX: 0, icons: 1);

		Assert.Equal(75, width, 3);
	}

	[Fact]
	public void Many_icons_never_make_the_row_wider_than_the_card()
	{
		var width = CardShape.IconRowWidth(CardWidth, CardHeight, new[] { (300.0, 1000.0) }, 3, alignX: 0, icons: 20);

		Assert.Equal(CardWidth, width, 3);
	}

	[Fact]
	public void Without_windows_the_row_is_as_wide_as_the_card()
	{
		Assert.Equal(CardWidth, CardShape.IconRowWidth(CardWidth, CardHeight, Array.Empty<(double, double)>(), 3, 0, 0));
	}
}
