using StageManager.Core;

namespace StageManager.Tests;

public class SnapPlannerTests
{
	private const long Monitor = 1;
	private const long OtherMonitor = 2;

	private static readonly Area Left = new(0, 0, 960, 1152);
	private static readonly Area Right = new(960, 0, 1920, 1152);
	private static readonly Area TopRight = new(960, 0, 1920, 576);
	private static readonly Area BottomRight = new(960, 576, 1920, 1152);
	private static readonly Area Floating = new(400, 200, 1400, 900);

	private static SnapWindow Arranged(long handle, Area frame, long monitor = Monitor, bool inSnapGroup = false) => new(handle, frame, true, monitor, inSnapGroup);

	private static SnapStage Stage(params SnapWindow[] windows) => new(Guid.NewGuid(), false, 0, windows);

	private static SnapStage SnapStage(params SnapWindow[] windows) => new(Guid.NewGuid(), true, Monitor, windows);

	[Fact]
	public void A_window_snapped_next_to_a_snapped_window_of_the_stage_creates_a_snap_group()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(2, Right), Stage(Arranged(1, Left)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Create, plan.Kind);
		Assert.Equal(new long[] { 1 }, plan.Anchors);
		Assert.Equal(new long[] { 2 }, plan.Joining);
	}

	[Fact]
	public void A_window_of_the_stage_does_not_join_its_own_scene()
	{
		// two windows of the same scene never form a group on their own
		var plan = SnapPlanner.PlanJoin(Arranged(2, Right), Stage(Arranged(1, Left), Arranged(2, Right)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("already", plan.Reason);
	}

	[Fact]
	public void A_window_that_is_not_arranged_does_not_join()
	{
		var plan = SnapPlanner.PlanJoin(new SnapWindow(2, Right, false, Monitor), Stage(Arranged(1, Left)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
	}

	[Fact]
	public void Without_snapped_windows_on_the_stage_nothing_happens()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(2, Right), Stage(new SnapWindow(1, Floating, false, Monitor)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("no arranged", plan.Reason);
	}

	[Fact]
	public void A_window_covering_a_snapped_window_of_the_stage_does_not_join()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(2, Left), Stage(Arranged(1, Left)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("overlaps", plan.Reason);
	}

	[Fact]
	public void A_window_far_away_does_not_join()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(2, new Area(1500, 0, 1920, 400)), Stage(Arranged(1, new Area(0, 0, 500, 400))), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("not next to", plan.Reason);
	}

	[Fact]
	public void Windows_on_another_monitor_are_no_anchors()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(2, Right), Stage(Arranged(1, Left, OtherMonitor)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
	}

	[Fact]
	public void A_snap_group_only_takes_windows_of_its_monitor()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(3, Right, OtherMonitor), SnapStage(Arranged(1, Left), Arranged(2, new Area(0, 0, 10, 10))), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("monitor", plan.Reason);
	}

	[Fact]
	public void Snap_Assist_windows_placed_before_the_last_zone_join_as_well()
	{
		// left half on the stage, the user fills top right and then bottom right with Snap Assist: only bottom right gets the focus
		var plan = SnapPlanner.PlanJoin(Arranged(3, BottomRight), Stage(Arranged(1, Left)), null, new[] { Arranged(2, TopRight) });

		Assert.Equal(SnapJoinKind.Create, plan.Kind);
		Assert.Equal(new long[] { 1 }, plan.Anchors);
		Assert.Equal(new long[] { 2, 3 }.OrderBy(x => x), plan.Joining.OrderBy(x => x));
	}

	[Fact]
	public void Candidates_that_are_not_arranged_or_in_another_snap_group_or_overlapping_stay_out()
	{
		var candidates = new[]
		{
			new SnapWindow(4, TopRight, false, Monitor),
			Arranged(5, TopRight, inSnapGroup: true),
			Arranged(6, new Area(100, 100, 900, 900)),
			Arranged(7, TopRight, OtherMonitor)
		};

		var plan = SnapPlanner.PlanJoin(Arranged(3, BottomRight), Stage(Arranged(1, Left)), null, candidates);

		Assert.Equal(new long[] { 3 }, plan.Joining);
	}

	[Fact]
	public void A_window_snapped_next_to_a_snap_group_on_the_stage_extends_it()
	{
		var plan = SnapPlanner.PlanJoin(Arranged(3, BottomRight), SnapStage(Arranged(1, Left), Arranged(2, TopRight)), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Extend, plan.Kind);
		Assert.Equal(new long[] { 3 }, plan.Joining);
	}

	[Fact]
	public void A_window_of_another_snap_group_merges_the_layout_into_its_group()
	{
		var group = new SnapGroupInfo(Guid.NewGuid(), Monitor, new[] { (3L, Right), (4L, new Area(0, 0, 960, 576)) });

		var plan = SnapPlanner.PlanJoin(Arranged(3, Right, inSnapGroup: true), Stage(Arranged(1, Left)), group, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Merge, plan.Kind);
		Assert.Equal(group.Id, plan.TargetGroup);
		Assert.Equal(new long[] { 1 }, plan.Anchors);
		Assert.Equal(new long[] { 3 }, plan.Joining);

		// window 4 had the top left quarter, the left half of the stage takes its place now
		Assert.Equal(new long[] { 4 }, plan.ReturnHome);
	}

	[Fact]
	public void Members_of_the_merged_group_that_do_not_overlap_the_new_layout_stay()
	{
		// the group had a third window on the right half of the screen, which still is free
		var group = new SnapGroupInfo(Guid.NewGuid(), Monitor, new[] { (3L, TopRight), (4L, BottomRight) });

		var plan = SnapPlanner.PlanJoin(Arranged(3, TopRight, inSnapGroup: true), Stage(Arranged(1, Left)), group, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Merge, plan.Kind);
		Assert.Empty(plan.ReturnHome);
	}

	[Fact]
	public void A_snap_group_of_the_window_on_another_monitor_is_not_merged()
	{
		var group = new SnapGroupInfo(Guid.NewGuid(), OtherMonitor, new[] { (3L, Right) });

		var plan = SnapPlanner.PlanJoin(Arranged(3, Right, inSnapGroup: true), Stage(Arranged(1, Left)), group, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
	}

	[Fact]
	public void Resizing_never_takes_a_window_out_of_the_group()
	{
		var members = new[] { new SnapMember(1, true, false, true, Left, new Area(0, 0, 800, 1152)) };

		Assert.Empty(SnapPlanner.Leaving(members));
	}

	[Fact]
	public void Dragging_or_maximizing_takes_a_window_out_of_the_group()
	{
		var members = new[]
		{
			new SnapMember(1, true, false, true, Left, new Area(200, 100, 1160, 1252)),
			new SnapMember(2, true, true, false, Right, new Area(-8, -8, 1928, 1160)),
			new SnapMember(3, true, false, false, TopRight, TopRight)
		};

		Assert.Equal(new long[] { 1, 2 }, SnapPlanner.Leaving(members));
	}

	[Fact]
	public void A_window_that_was_not_moved_by_the_user_does_not_leave_even_if_it_is_somewhere_else()
	{
		// e.g. parked or moved back by StageManager
		var members = new[] { new SnapMember(1, true, false, false, Left, new Area(-2000, 0, -1040, 1152)) };

		Assert.Empty(SnapPlanner.Leaving(members));
	}

	[Fact]
	public void Windows_outside_of_the_layout_never_leave()
	{
		// a card that was dragged onto a snap group: its maximized window stays in the group
		var members = new[] { new SnapMember(1, false, true, true, null, new Area(-8, -8, 1928, 1160)) };

		Assert.Empty(SnapPlanner.Leaving(members));
	}
}
