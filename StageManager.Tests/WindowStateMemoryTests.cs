using StageManager.Core;

namespace StageManager.Tests;

public class WindowStateMemoryTests
{
	// the numbers of the log: Claude maximized on a 3440 x 1440 monitor, restored by the drag before the event was handled
	private static readonly WindowSnapshot Maximized = new(new Area(-8, -8, 3448, 1400), true, false, new Area(523, 0, 2457, 1136));
	private static readonly WindowSnapshot RestoredByTheDrag = new(new Area(523, -8, 2457, 1128), false, false);

	// Notepad snapped to the bottom left, unsnapped by the drag
	private static readonly WindowSnapshot Snapped = new(new Area(-7, 696, 1828, 1399), false, true);
	private static readonly WindowSnapshot UnsnappedByTheDrag = new(new Area(380, 696, 1354, 1492), false, false);

	[Fact]
	public void The_start_of_a_drag_uses_the_state_from_before_the_drag()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Maximized);

		var (start, remembered) = m.BeginMove(1, RestoredByTheDrag);

		Assert.True(remembered);
		Assert.Equal(Maximized, start);
	}

	[Fact]
	public void A_snapped_window_starts_snapped()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Snapped);

		Assert.Equal(Snapped, m.BeginMove(1, UnsnappedByTheDrag).Snapshot);
	}

	[Fact]
	public void Without_a_remembered_state_the_live_one_is_used()
	{
		var m = new WindowStateMemory();

		var (start, remembered) = m.BeginMove(1, RestoredByTheDrag);

		Assert.False(remembered);
		Assert.Equal(RestoredByTheDrag, start);
	}

	[Fact]
	public void Changes_during_the_move_are_not_remembered()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Maximized);
		m.BeginMove(1, RestoredByTheDrag);

		Assert.False(m.Remember(1, RestoredByTheDrag));
		Assert.Equal(Maximized, m.Get(1));
		Assert.True(m.IsMoving(1));
	}

	[Fact]
	public void After_the_move_the_new_state_is_remembered()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Snapped);
		m.BeginMove(1, UnsnappedByTheDrag);
		m.EndMove(1);

		Assert.True(m.Remember(1, UnsnappedByTheDrag));
		Assert.Equal(UnsnappedByTheDrag, m.BeginMove(1, Snapped).Snapshot);
	}

	[Fact]
	public void The_latest_state_wins()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Snapped);
		m.Remember(1, Maximized);

		Assert.Equal(Maximized, m.BeginMove(1, RestoredByTheDrag).Snapshot);
	}

	[Fact]
	public void Windows_are_remembered_one_by_one()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Maximized);
		m.Remember(2, Snapped);
		m.BeginMove(1, RestoredByTheDrag);

		Assert.True(m.Remember(2, UnsnappedByTheDrag));
		Assert.Equal(UnsnappedByTheDrag, m.Get(2));
	}

	[Fact]
	public void A_forgotten_window_starts_fresh()
	{
		var m = new WindowStateMemory();
		m.Remember(1, Maximized);
		m.BeginMove(1, RestoredByTheDrag);
		m.Forget(1);

		Assert.Null(m.Get(1));
		Assert.False(m.IsMoving(1));
		Assert.False(m.BeginMove(1, RestoredByTheDrag).Remembered);
	}
}

public class SnapRejoinTests
{
	private static readonly Guid Group = Guid.NewGuid();
	private static readonly Guid OtherGroup = Guid.NewGuid();

	// the layout of the log: Claude right, Terminal top left, Notepad bottom left, with the invisible borders of 7 pixels
	private static readonly Area Claude = new(1814, 0, 3447, 1399);
	private static readonly Area Terminal = new(-7, 0, 1828, 703);
	private static readonly Area Notepad = new(-7, 696, 1828, 1399);

	[Fact]
	public void A_window_whose_place_is_free_takes_it_again()
	{
		var decision = SnapRejoin.Decide(new SnapHome(Group, Notepad), Group, false, new[] { Claude, Terminal });

		Assert.Equal(SnapRejoinDecision.Rejoin, decision);
	}

	[Fact]
	public void A_window_whose_place_another_window_took_stays_out_of_the_layout()
	{
		// the terminal was made as tall as the screen after the notepad had left
		var tallTerminal = new Area(-7, 0, 1828, 1399);

		var decision = SnapRejoin.Decide(new SnapHome(Group, Notepad), Group, false, new[] { Claude, tallTerminal });

		Assert.Equal(SnapRejoinDecision.Occupied, decision);
	}

	[Fact]
	public void A_window_that_was_in_another_group_has_no_place_here()
	{
		var decision = SnapRejoin.Decide(new SnapHome(OtherGroup, Notepad), Group, false, new[] { Claude, Terminal });

		Assert.Equal(SnapRejoinDecision.NoFormerPlace, decision);
	}

	[Fact]
	public void A_window_that_never_was_in_a_group_has_no_place()
	{
		Assert.Equal(SnapRejoinDecision.NoFormerPlace, SnapRejoin.Decide(null, Group, false, new[] { Claude, Terminal }));
	}

	[Fact]
	public void A_window_in_the_layout_stays_as_it_is()
	{
		Assert.Equal(SnapRejoinDecision.InLayout, SnapRejoin.Decide(new SnapHome(Group, Notepad), Group, true, new[] { Claude, Terminal }));
	}

	[Fact]
	public void Neighbours_that_overlap_by_their_invisible_borders_leave_the_place_free()
	{
		Assert.True(SnapRejoin.IsFree(Notepad, new[] { Terminal, Claude }));
	}

	[Fact]
	public void Larger_invisible_borders_need_a_larger_shrink()
	{
		// at 200 % the borders are 14 pixels wide, neighbours overlap by 28
		var top = new Area(-14, 0, 1834, 714);
		var bottom = new Area(-14, 686, 1834, 1400);

		Assert.False(SnapRejoin.IsFree(bottom, new[] { top }));
		Assert.True(SnapRejoin.IsFree(bottom, new[] { top }, SnapGeometry.LayoutShrink * 2));
	}

	[Fact]
	public void A_window_that_covers_a_part_of_the_place_takes_it()
	{
		var overlapping = new Area(-7, 600, 900, 1100);

		Assert.False(SnapRejoin.IsFree(Notepad, new[] { overlapping }));
	}

	[Fact]
	public void An_empty_layout_leaves_every_place_free()
	{
		Assert.True(SnapRejoin.IsFree(Notepad, Array.Empty<Area>()));
	}
}

public class SnapAssistSpillTests
{
	private const long Monitor = 1;

	// the numbers of the log: Terminal snapped left, Claude still arranged on the right, Snap Assist put Teams into the gap between them.
	// The gap is 101 pixels wide, Teams cannot be narrower than 361
	private static readonly SnapWindow Terminal = new(1, new Area(0, 0, 1720, 1392), true, Monitor);
	private static readonly SnapWindow Claude = new(2, new Area(1821, 0, 3440, 1392), true, Monitor);
	private static readonly SnapWindow Teams = new(3, new Area(1720, 0, 2081, 1392), true, Monitor);

	private static SnapStage Stage(params SnapWindow[] windows) => new(Guid.NewGuid(), false, 0, windows);

	[Fact]
	public void A_window_Snap_Assist_put_into_a_narrow_gap_joins_the_stage()
	{
		var plan = SnapPlanner.PlanJoin(Teams, Stage(Terminal, Claude), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Create, plan.Kind);
		Assert.Equal(new long[] { 3 }, plan.Joining);
		Assert.Contains(1L, plan.Anchors);
		Assert.Contains(2L, plan.Anchors);
	}

	[Fact]
	public void A_window_snapped_onto_the_place_of_a_window_of_the_stage_does_not_join()
	{
		var onClaude = new SnapWindow(3, new Area(1720, 0, 3440, 1392), true, Monitor);

		var plan = SnapPlanner.PlanJoin(onClaude, Stage(Terminal, Claude), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
		Assert.Contains("overlaps", plan.Reason);
	}

	[Fact]
	public void A_window_that_covers_half_of_a_window_of_the_stage_does_not_join()
	{
		var half = new SnapWindow(3, new Area(1720, 0, 2630, 1392), true, Monitor);

		var plan = SnapPlanner.PlanJoin(half, Stage(Terminal, Claude), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
	}

	[Fact]
	public void A_window_that_overlaps_without_touching_another_window_does_not_join()
	{
		// spills over Claude, but is not next to the terminal
		var floating = new SnapWindow(3, new Area(1760, 0, 2081, 1392), true, Monitor);

		var plan = SnapPlanner.PlanJoin(floating, Stage(Terminal, Claude), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.None, plan.Kind);
	}

	[Fact]
	public void A_spill_is_measured_against_the_window_it_covers()
	{
		var claude = Claude.Frame;

		Assert.True(SnapGeometry.SpillsOver(Teams.Frame, new[] { Terminal.Frame, claude }));
		Assert.False(SnapGeometry.SpillsOver(Teams.Frame, new[] { Terminal.Frame }));
	}

	[Fact]
	public void A_window_next_to_the_stage_without_overlap_still_joins_as_before()
	{
		var claudeLeft = new SnapWindow(2, new Area(2081, 0, 3440, 1392), true, Monitor);

		var plan = SnapPlanner.PlanJoin(Teams, Stage(Terminal, claudeLeft), null, Array.Empty<SnapWindow>());

		Assert.Equal(SnapJoinKind.Create, plan.Kind);
		Assert.Equal(new long[] { 3 }, plan.Joining);
	}
}
