namespace StageManager.Core;

/// <summary>Where a window sits on its card when its proportions leave room: 0 is left or top, 1 is right or bottom.</summary>
public readonly record struct CardAlignment(double X, double Y)
{
	public static readonly CardAlignment Centered = new(0.5, 0.5);

	/// <summary>
	/// The bottom corner at the screen edge. The icons of the app sit on that corner, so they touch the window however wide or tall it is,
	/// and the windows of all cards share one line to look along.
	/// </summary>
	public static CardAlignment For(SidebarSide side) => new(side == SidebarSide.Right ? 1 : 0, 1);
}

/// <summary>How tall a card is: as tall as its windows are on it, so the space between two cards is the same everywhere.</summary>
public static class CardShape
{
	/// <summary>A card of very flat windows still stays this tall, otherwise it would be a sliver.</summary>
	public const double MinHeight = 48;

	/// <param name="cardWidth">Width of the card, the windows are fitted into it.</param>
	/// <param name="maxHeight">The tallest a card gets: the height the layout works out for the sidebar.</param>
	/// <param name="backToFront">Width and height of each window of the card, back to front. 0 for a window of unknown size.</param>
	/// <param name="maxVisible">How many of the windows show as thumbnails, the frontmost ones.</param>
	public static double Height(double cardWidth, double maxHeight, IReadOnlyList<(double Width, double Height)> backToFront, int maxVisible)
	{
		if (backToFront.Count == 0 || !(maxHeight > 0))
			return maxHeight;

		var shown = Math.Clamp(maxVisible, 1, backToFront.Count);
		var inset = (shown - 1) * ThumbnailStack.Step;
		var slotWidth = Math.Max(1, cardWidth - inset);
		var slotHeight = Math.Max(1, maxHeight - inset);

		var tallest = 0.0;
		for (var i = backToFront.Count - shown; i < backToFront.Count; i++)
		{
			var source = backToFront[i];
			tallest = Math.Max(tallest, AspectFit.Calculate(slotWidth, slotHeight, source.Width, source.Height).Height);
		}

		return Math.Clamp(tallest + inset, Math.Min(MinHeight, maxHeight), maxHeight);
	}

	/// <summary>Width of an app icon below the card, with its gap.</summary>
	public const double IconWidth = 31;

	/// <summary>Width of the close button below the card, with a gap to the icons.</summary>
	public const double CloseButtonWidth = 26;

	/// <summary>
	/// How wide the row of icons and the close button below a card is: from the screen edge to the far edge of the windows as they are
	/// drawn on the card. A narrow window leaves room beside it, the close button must sit next to the window and not out in that room,
	/// otherwise the pointer leaves the card on its way there. Never narrower than the icons and the button need.
	/// </summary>
	/// <param name="cardWidth">Width of the card.</param>
	/// <param name="cardHeight">Height of the card, as <see cref="Height"/> worked it out.</param>
	/// <param name="backToFront">Width and height of each window of the card, back to front. 0 for a window of unknown size.</param>
	/// <param name="maxVisible">How many of the windows show as thumbnails, the frontmost ones.</param>
	/// <param name="alignX">Where the windows sit on the card, see <see cref="CardAlignment.X"/>: 0 at the left screen edge, 1 at the right.</param>
	/// <param name="icons">Number of icons in the row.</param>
	public static double IconRowWidth(double cardWidth, double cardHeight, IReadOnlyList<(double Width, double Height)> backToFront, int maxVisible, double alignX, int icons)
	{
		if (backToFront.Count == 0 || !(cardWidth > 0))
			return Math.Max(0, cardWidth);

		var slots = ThumbnailStack.Calculate(cardWidth, cardHeight, backToFront.Count, maxVisible);
		var left = double.MaxValue;
		var right = double.MinValue;

		for (var i = 0; i < slots.Length; i++)
		{
			var slot = slots[i];
			if (!slot.IsVisible)
				continue;

			var fit = AspectFit.Calculate(slot.Width, slot.Height, backToFront[i].Width, backToFront[i].Height, alignX, 1);
			left = Math.Min(left, slot.OffsetX + fit.X);
			right = Math.Max(right, slot.OffsetX + fit.X + fit.Width);
		}

		if (left > right)
			return cardWidth;

		// measured from the screen edge, where the windows and the icons sit
		var width = alignX < 0.5 ? right : cardWidth - left;
		var needed = Math.Max(0, icons) * IconWidth + CloseButtonWidth;

		return Math.Clamp(Math.Max(width, needed), 0, cardWidth);
	}
}
