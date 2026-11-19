namespace StageManager.Core;

/// <summary>A window as the snap group logic sees it.</summary>
/// <param name="Handle">The window handle as a number.</param>
/// <param name="Frame">The visible frame, without the invisible borders.</param>
/// <param name="Arranged">The window is on the screen and arranged (snapped) by Windows.</param>
/// <param name="Monitor">The monitor the window is on.</param>
/// <param name="InSnapGroup">The window belongs to a snap group.</param>
public sealed record SnapWindow(long Handle, Area Frame, bool Arranged, long Monitor, bool InSnapGroup = false);

/// <summary>The scene on the stage.</summary>
/// <param name="IsSnapGroup">The scene is a snap group.</param>
/// <param name="Monitor">The monitor of the snap group, 0 for other scenes.</param>
public sealed record SnapStage(Guid Id, bool IsSnapGroup, long Monitor, IReadOnlyList<SnapWindow> Windows);

/// <summary>An existing snap group the window that was snapped belongs to.</summary>
/// <param name="Members">The windows of the group with their rectangle: the stored one if the window is not on the screen.</param>
public sealed record SnapGroupInfo(Guid Id, long Monitor, IReadOnlyList<(long Handle, Area Rect)> Members);

public enum SnapJoinKind
{
	/// <summary>Nothing happens, <see cref="SnapJoinPlan.Reason"/> says why.</summary>
	None,

	/// <summary>The anchors of the scene on the stage and the joining windows form a new snap group.</summary>
	Create,

	/// <summary>The joining windows join the snap group on the stage.</summary>
	Extend,

	/// <summary>The window belongs to another snap group: the new layout goes into that group, members it covers go home.</summary>
	Merge
}

/// <param name="Anchors">Windows of the stage that are part of the new layout.</param>
/// <param name="Joining">Windows that join the layout, the snapped window among them.</param>
/// <param name="ReturnHome">Members of the merged group whose place the new layout takes. They go back to the group of their app.</param>
/// <param name="TargetGroup">The snap group of the window for <see cref="SnapJoinKind.Merge"/>.</param>
public sealed record SnapJoinPlan(SnapJoinKind Kind, string Reason, IReadOnlyList<long> Anchors, IReadOnlyList<long> Joining, IReadOnlyList<long> ReturnHome, Guid? TargetGroup)
{
	public static SnapJoinPlan Nothing(string reason) => new(SnapJoinKind.None, reason, Array.Empty<long>(), Array.Empty<long>(), Array.Empty<long>(), null);
}

/// <summary>A window of the snap group on the stage, to find out whether it leaves the group.</summary>
/// <param name="InLayout">The window is part of the stored layout. Windows added to the group another way never leave it this way.</param>
/// <param name="MovedByUser">The user moved or resized the window since the last check.</param>
/// <param name="Reference">Where the window was before: the stored layout or the rectangle at the start of the move.</param>
/// <param name="Current">The window rectangle now.</param>
public sealed record SnapMember(long Handle, bool InLayout, bool Maximized, bool MovedByUser, Area? Reference, Area Current);

/// <summary>
/// Decides what happens when windows are snapped: which windows form a snap group, when groups are merged and when a window leaves
/// a group. Knows nothing about Win32, the caller collects the facts.
/// </summary>
public static class SnapPlanner
{
	/// <summary>
	/// A window was snapped while <paramref name="stage"/> is on the stage. It joins the snapped windows of the stage if it touches them,
	/// directly or through other snapped windows, which join as well: Snap Assist only activates the window chosen for the last zone.
	/// </summary>
	/// <param name="window">The window that was snapped.</param>
	/// <param name="windowGroup">The snap group the window belongs to, null if it has none.</param>
	/// <param name="candidates">Other windows on the screen that might have been snapped next to the layout as well.</param>
	public static SnapJoinPlan PlanJoin(SnapWindow window, SnapStage stage, SnapGroupInfo? windowGroup, IEnumerable<SnapWindow> candidates)
	{
		if (stage.Windows.Any(w => w.Handle == window.Handle))
			return SnapJoinPlan.Nothing("already in the current scene");

		if (!window.Arranged)
			return SnapJoinPlan.Nothing("not on the screen or not arranged");

		if (stage.IsSnapGroup && stage.Monitor != window.Monitor)
			return SnapJoinPlan.Nothing("the snap group of the stage is on a different monitor");

		if (windowGroup is not null && windowGroup.Monitor != window.Monitor)
			return SnapJoinPlan.Nothing("the snap group of the window is on a different monitor");

		var anchors = stage.Windows.Where(w => w.Arranged && w.Monitor == window.Monitor).ToList();
		if (anchors.Count == 0)
			return SnapJoinPlan.Nothing("no arranged windows on the stage");

		if (anchors.Any(a => SnapGeometry.OverlapsSignificantly(window.Frame, a.Frame)))
			return SnapJoinPlan.Nothing("overlaps an arranged window of the stage");

		var others = candidates
			.Where(c => c.Handle != window.Handle && stage.Windows.All(w => w.Handle != c.Handle))
			.Where(c => !c.InSnapGroup && c.Arranged && c.Monitor == window.Monitor)
			.Where(c => !SnapGeometry.OverlapsSignificantly(c.Frame, window.Frame) && !anchors.Any(a => SnapGeometry.OverlapsSignificantly(c.Frame, a.Frame)))
			.GroupBy(c => c.Handle)
			.Select(g => g.First())
			.ToList();

		var all = anchors.Concat(others).Append(window).ToList();
		var component = SnapGeometry.Connected(window, all, w => w.Frame, HandleComparer.Instance);

		var anchorHandles = anchors.Select(a => a.Handle).ToHashSet();
		var connectedAnchors = component.Where(c => anchorHandles.Contains(c.Handle)).Select(c => c.Handle).ToList();
		if (connectedAnchors.Count == 0)
			return SnapJoinPlan.Nothing("not next to an arranged window of the stage");

		var joining = component.Where(c => !anchorHandles.Contains(c.Handle)).Select(c => c.Handle).ToList();

		if (windowGroup is not null)
		{
			var layout = component.ToList();
			var returnHome = windowGroup.Members
				.Where(m => layout.All(l => l.Handle != m.Handle))
				.Where(m => layout.Any(l => SnapGeometry.LayoutOverlaps(m.Rect, l.Frame)))
				.Select(m => m.Handle)
				.ToList();

			return new SnapJoinPlan(SnapJoinKind.Merge, "", connectedAnchors, joining, returnHome, windowGroup.Id);
		}

		var kind = stage.IsSnapGroup ? SnapJoinKind.Extend : SnapJoinKind.Create;
		return new SnapJoinPlan(kind, "", connectedAnchors, joining, Array.Empty<long>(), null);
	}

	/// <summary>
	/// The windows that leave the snap group: maximized ones and ones that were dragged at the title bar. Resizing never takes a
	/// window out of the group.
	/// </summary>
	public static IReadOnlyList<long> Leaving(IEnumerable<SnapMember> members) =>
		members
			.Where(m => m.InLayout)
			.Where(m => m.Maximized || (m.MovedByUser && m.Reference is Area before && SnapGeometry.WasDragged(before, m.Current)))
			.Select(m => m.Handle)
			.ToList();

	private sealed class HandleComparer : IEqualityComparer<SnapWindow>
	{
		public static readonly HandleComparer Instance = new();

		public bool Equals(SnapWindow? x, SnapWindow? y) => x?.Handle == y?.Handle;

		public int GetHashCode(SnapWindow obj) => obj.Handle.GetHashCode();
	}
}
