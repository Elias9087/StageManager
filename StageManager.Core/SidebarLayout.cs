namespace StageManager.Core;

public sealed record SidebarLayoutInput(
	double AreaHeight,
	int MaxScenes,
	double Aspect,
	double FixedSidebarWidth,
	double FixedThumbnailWidth,
	double FixedThumbnailHeight,
	int SceneCount = 0);

public sealed record SidebarLayoutResult(
	double ThumbnailWidth,
	double ThumbnailHeight,
	double SidebarWidth,
	int EffectiveMaxScenes,
	bool IsLimited,
	double CardGap = SidebarLayout.MinCardGap,
	bool IsScrolling = false);

/// <summary>
/// Calculates how large the thumbnails can be so that the wanted number of scenes fits into the height of the screen. With more
/// scenes the cards get smaller, down to <see cref="MinShrink"/> of their full size, beyond that the sidebar scrolls. No card is hidden.
/// All values are device independent units. The constants have to match the sidebar template in MainWindow.xaml.
/// </summary>
public static class SidebarLayout
{
	/// <summary>Space above the first and below the last card.</summary>
	public const double VerticalMargin = 24;

	/// <summary>Icon row below a thumbnail: 5 margin + 28 icon height.</summary>
	public const double IconRowHeight = 33;

	/// <summary>The least space below every card. Where the screen has room left, the space grows up to <see cref="MaxCardGap"/>.</summary>
	public const double MinCardGap = 32;

	/// <summary>The most space below a card. Beyond it the cards stay together and the stack is centered, like on macOS.</summary>
	public const double MaxCardGap = 72;

	/// <summary>Space between the screen edge and the thumbnails.</summary>
	public const double EdgePadding = 12;

	/// <summary>Space between the thumbnails and the inner edge of the sidebar, leaves room for the shadow.</summary>
	public const double ShadowRoom = 24;

	public const double MinThumbnailWidth = 96;
	public const double MinThumbnailHeight = 60;
	public const double MaxThumbnailWidth = 320;
	public const double MaxThumbnailHeight = 200;

	/// <summary>How small the cards get with more scenes than have room at full size, as a share of the full size.</summary>
	public const double MinShrink = 0.6;

	/// <param name="sceneCount">The cards in the sidebar, 0 for as many as fit at full size.</param>
	public static SidebarLayoutResult Calculate(SidebarSettings sidebar, double areaHeight, int sceneCount = 0) =>
		Calculate(new SidebarLayoutInput(
			areaHeight,
			sidebar.MaxScenes,
			sidebar.ThumbnailAspect,
			sidebar.Width,
			sidebar.ThumbnailWidth,
			sidebar.ThumbnailHeight,
			sceneCount));

	public static SidebarLayoutResult Calculate(SidebarLayoutInput input)
	{
		var requested = Math.Clamp(input.MaxScenes, SidebarSettings.MinScenes, SidebarSettings.MaxScenesLimit);
		var aspect = double.IsFinite(input.Aspect)
			? Math.Clamp(input.Aspect, SidebarSettings.MinAspect, SidebarSettings.MaxAspect)
			: SidebarSettings.DefaultAspect;
		var available = Math.Max(0, input.AreaHeight - 2 * VerticalMargin);
		var chrome = IconRowHeight + MinCardGap;

		double width;
		double height;

		if (input.FixedThumbnailWidth > 0 && input.FixedThumbnailHeight > 0)
		{
			width = input.FixedThumbnailWidth;
			height = input.FixedThumbnailHeight;
		}
		else
		{
			height = Math.Clamp(available / requested - chrome, MinThumbnailHeight, MaxThumbnailHeight);
			width = height * aspect;

			if (width > MaxThumbnailWidth)
			{
				width = MaxThumbnailWidth;
				height = width / aspect;
			}
		}

		// a fixed sidebar width wins over the thumbnail size, the thumbnail shrinks and keeps its proportions
		if (input.FixedSidebarWidth > 0)
		{
			var room = input.FixedSidebarWidth - EdgePadding - ShadowRoom;
			if (room > 0 && width > room)
			{
				height *= room / width;
				width = room;
			}
		}

		var fitting = (int)Math.Floor(available / (height + chrome));
		var effective = Math.Clamp(fitting, 1, requested);

		// the sidebar keeps its width when the cards get smaller, so it does not jump with the number of scenes
		var sidebarWidth = input.FixedSidebarWidth > 0
			? input.FixedSidebarWidth
			: Math.Clamp(width + EdgePadding + ShadowRoom, SidebarSettings.MinWidth, SidebarSettings.MaxWidth);

		// more cards than have room at full size: they get smaller so all of them fit, but not below the least size
		var shown = Math.Max(effective, input.SceneCount);
		if (shown > effective && height > 0)
		{
			var scale = Math.Clamp((available / shown - chrome) / height, MinShrink, 1);
			width *= scale;
			height *= scale;
		}

		// room that is left once the cards are as large as they get is shared out between them, but never less than the minimum
		var gap = Math.Clamp(available / shown - height - IconRowHeight, MinCardGap, MaxCardGap);
		var scrolling = shown * (height + IconRowHeight + gap) - gap > available + 0.5;

		return new SidebarLayoutResult(width, height, sidebarWidth, effective, effective < requested, gap, scrolling);
	}
}
