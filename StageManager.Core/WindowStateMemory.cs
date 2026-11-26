namespace StageManager.Core;

/// <summary>The state of a window on the screen at one moment.</summary>
/// <param name="Rect">The window rectangle, with the invisible borders.</param>
/// <param name="Maximized">The window is maximized.</param>
/// <param name="Arranged">The window is arranged (snapped) by Windows.</param>
/// <param name="Normal">The restored rectangle of a maximized window as Windows keeps it (workspace coordinates), null for other windows.</param>
public sealed record WindowSnapshot(Area Rect, bool Maximized, bool Arranged, Area? Normal = null);

/// <summary>
/// Remembers the last state each window had on the screen while nobody moved it. When the user starts to drag a window at its title bar,
/// Windows changes it at once: a maximized window is restored, a snapped one gets its size from before it was snapped. The event that
/// tells about the drag arrives later, so the state read then is already the changed one. The state remembered before is what the
/// window has to go back to.
/// <para>
/// Windows reports the start of a move before the changes the move makes, and the events arrive in order. So every change remembered
/// before the start of a move was made before it, however late it was handled.
/// </para>
/// </summary>
public sealed class WindowStateMemory
{
	private readonly Dictionary<long, WindowSnapshot> _last = new();
	private readonly HashSet<long> _moving = new();

	/// <summary>Remembers the state of a window on the screen. Ignored while the window is moved or resized by the user.</summary>
	/// <returns>True if it was remembered.</returns>
	public bool Remember(long window, WindowSnapshot snapshot)
	{
		if (_moving.Contains(window))
			return false;

		_last[window] = snapshot;
		return true;
	}

	/// <summary>The user starts to move or resize the window: what it was before, the remembered state if there is one.</summary>
	/// <param name="live">The state read now, used if nothing was remembered.</param>
	public (WindowSnapshot Snapshot, bool Remembered) BeginMove(long window, WindowSnapshot live)
	{
		_moving.Add(window);
		return _last.TryGetValue(window, out var remembered) ? (remembered, true) : (live, false);
	}

	/// <summary>The move or resize is over, the state of the window is remembered again from now on.</summary>
	public void EndMove(long window) => _moving.Remove(window);

	public bool IsMoving(long window) => _moving.Contains(window);

	public WindowSnapshot? Get(long window) => _last.TryGetValue(window, out var s) ? s : null;

	/// <summary>The window is gone, its handle may be used for another one.</summary>
	public void Forget(long window)
	{
		_last.Remove(window);
		_moving.Remove(window);
	}
}

public enum SnapRejoinDecision
{
	/// <summary>The window was never part of the layout of this snap group.</summary>
	NoFormerPlace,

	/// <summary>The window is part of the layout already.</summary>
	InLayout,

	/// <summary>Another window of the group lies on its former place now.</summary>
	Occupied,

	/// <summary>The former place is free: the window takes it again.</summary>
	Rejoin
}

/// <summary>The place a window had in the layout of a snap group.</summary>
public readonly record struct SnapHome(Guid Group, Area Rect);

/// <summary>
/// Decides whether a window that comes into a snap group takes its former place in the layout again, as if it had never left.
/// </summary>
public static class SnapRejoin
{
	/// <param name="home">The last place of the window in a snap group, null if it never had one.</param>
	/// <param name="group">The snap group the window comes into.</param>
	/// <param name="alreadyInLayout">The window is part of the layout of the group already.</param>
	/// <param name="layout">The stored rectangles of the other windows in the layout of the group.</param>
	/// <param name="shrink">How much both rectangles shrink before they are compared: neighbours overlap by their invisible borders.</param>
	public static SnapRejoinDecision Decide(SnapHome? home, Guid group, bool alreadyInLayout, IEnumerable<Area> layout, double shrink = SnapGeometry.LayoutShrink)
	{
		if (alreadyInLayout)
			return SnapRejoinDecision.InLayout;

		if (home is not SnapHome h || h.Group != group)
			return SnapRejoinDecision.NoFormerPlace;

		return IsFree(h.Rect, layout, shrink) ? SnapRejoinDecision.Rejoin : SnapRejoinDecision.Occupied;
	}

	/// <summary>True if no rectangle of the layout covers the place, apart from the invisible borders next to it.</summary>
	public static bool IsFree(Area place, IEnumerable<Area> layout, double shrink = SnapGeometry.LayoutShrink) =>
		!layout.Any(r => SnapGeometry.OverlapsSignificantly(Shrink(place, shrink), Shrink(r, shrink), 0));

	private static Area Shrink(Area a, double by) => new(a.Left + by, a.Top + by, a.Right - by, a.Bottom - by);
}
