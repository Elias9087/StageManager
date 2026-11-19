namespace StageManager.Core;

/// <summary>
/// Geometry of windows that were arranged with the snap feature of Windows: which ones touch each other, which ones form a layout
/// and whether a window was dragged or only resized. Rectangles are in physical pixels.
/// </summary>
public static class SnapGeometry
{
	/// <summary>How far apart two window edges may be to count as touching.</summary>
	public const double AdjacencyTolerance = 12;

	/// <summary>How far a window may be off before it counts as moved.</summary>
	public const double LayoutTolerance = 2;

	/// <summary>How much both rectangles shrink before <see cref="LayoutOverlaps"/> compares them, so that neighbours do not overlap.</summary>
	public const double LayoutShrink = 8;

	/// <summary>True if the rectangles touch each other at one edge and share a part of it.</summary>
	public static bool AreAdjacent(Area a, Area b, double tolerance = AdjacencyTolerance)
	{
		var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
		var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

		var touchHorizontally = (Math.Abs(a.Right - b.Left) <= tolerance || Math.Abs(b.Right - a.Left) <= tolerance) && overlapY > tolerance;
		var touchVertically = (Math.Abs(a.Bottom - b.Top) <= tolerance || Math.Abs(b.Bottom - a.Top) <= tolerance) && overlapX > tolerance;

		return touchHorizontally || touchVertically;
	}

	/// <summary>True if the rectangles overlap by more than the tolerance in both directions.</summary>
	public static bool OverlapsSignificantly(Area a, Area b, double tolerance = AdjacencyTolerance)
	{
		var overlapX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
		var overlapY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

		return overlapX > tolerance && overlapY > tolerance;
	}

	/// <summary>
	/// Compares a stored window rectangle (with the invisible borders) with a visible frame. Both shrink a little, so that windows
	/// next to each other do not count as overlapping.
	/// </summary>
	public static bool LayoutOverlaps(Area windowRect, Area frame) =>
		OverlapsSignificantly(Shrink(windowRect, LayoutShrink), Shrink(frame, LayoutShrink));

	/// <summary>All items connected to <paramref name="start"/> through a chain of touching rectangles, the start first.</summary>
	public static List<T> Connected<T>(T start, IEnumerable<T> candidates, Func<T, Area> frameOf, IEqualityComparer<T>? comparer = null)
	{
		comparer ??= EqualityComparer<T>.Default;

		var remaining = candidates.Where(c => !comparer.Equals(c, start)).Distinct(comparer).ToList();
		var result = new List<T> { start };
		var queue = new Queue<T>();
		queue.Enqueue(start);

		while (queue.Count > 0)
		{
			var current = queue.Dequeue();
			var currentFrame = frameOf(current);

			foreach (var neighbour in remaining.Where(r => AreAdjacent(currentFrame, frameOf(r))).ToList())
			{
				remaining.Remove(neighbour);
				result.Add(neighbour);
				queue.Enqueue(neighbour);
			}
		}

		return result;
	}

	/// <summary>
	/// True if a window was dragged rather than resized. Resizing keeps at least one edge in place (three when a border is dragged,
	/// two for a corner), dragging the title bar moves all four.
	/// </summary>
	public static bool WasDragged(Area before, Area after, double tolerance = LayoutTolerance) =>
		Math.Abs(before.Left - after.Left) > tolerance
		&& Math.Abs(before.Top - after.Top) > tolerance
		&& Math.Abs(before.Right - after.Right) > tolerance
		&& Math.Abs(before.Bottom - after.Bottom) > tolerance;

	/// <summary>True if all edges are within the tolerance.</summary>
	public static bool IsSame(Area a, Area b, double tolerance = LayoutTolerance) =>
		Math.Abs(a.Left - b.Left) <= tolerance
		&& Math.Abs(a.Top - b.Top) <= tolerance
		&& Math.Abs(a.Right - b.Right) <= tolerance
		&& Math.Abs(a.Bottom - b.Bottom) <= tolerance;

	private static Area Shrink(Area a, double by) => new(a.Left + by, a.Top + by, a.Right - by, a.Bottom - by);
}
