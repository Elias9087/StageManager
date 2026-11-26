using StageManager.Core;

namespace StageManager.Tests;

public class WindowDropTargetsTests
{
	private static readonly SidebarGeometry Left = new(new Area(0, 0, 1920, 1040), 280, SidebarSide.Left);
	private static readonly SidebarGeometry Right = new(new Area(0, 0, 1920, 1040), 280, SidebarSide.Right);

	private static readonly Guid A = Guid.NewGuid();
	private static readonly Guid B = Guid.NewGuid();

	// two cards above each other in the left sidebar, a gap of 60 pixels between them
	private static readonly (Guid, Area)[] Cards =
	{
		(A, new Area(20, 200, 260, 380)),
		(B, new Area(20, 440, 260, 620))
	};

	// the same cards in the right sidebar
	private static readonly (Guid, Area)[] RightCards =
	{
		(A, new Area(1660, 200, 1900, 380)),
		(B, new Area(1660, 440, 1900, 620))
	};

	private static WindowDropTarget Update(WindowDropTargets t, double x, double y, bool shown = true, bool allowNewCard = true, SidebarGeometry? geometry = null,
		IReadOnlyList<(Guid, Area)>? cards = null)
	{
		t.Update(x, y, geometry ?? Left, cards ?? Cards, shown, allowNewCard);
		return t.Target;
	}

	[Fact]
	public void A_card_under_the_pointer_is_the_target()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 290));
		Assert.Equal(WindowDropTarget.OnCard(B), Update(t, 140, 530));
	}

	[Fact]
	public void A_free_place_in_the_sidebar_makes_a_new_card()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.NewCard, Update(t, 140, 410));
		Assert.Equal(WindowDropTarget.NewCard, Update(t, 140, 900));
		Assert.Equal(WindowDropTarget.NewCard, Update(t, 140, 100));
	}

	[Fact]
	public void A_window_alone_on_the_stage_gets_no_new_card_but_can_go_onto_a_card()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, 140, 410, allowNewCard: false));
		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 290, allowNewCard: false));
	}

	[Fact]
	public void Outside_the_sidebar_there_is_no_target()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, 281, 290));
		Assert.Equal(WindowDropTarget.None, Update(t, 900, 500));
	}

	[Fact]
	public void A_hidden_sidebar_has_no_targets()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, 140, 290, shown: false));
		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 290, shown: true));
	}

	[Theory]
	[InlineData(0, 290)]
	[InlineData(7, 290)]
	[InlineData(140, 0)]
	[InlineData(140, 7)]
	[InlineData(3, 3)]
	public void The_screen_edge_and_the_top_belong_to_Aero_Snap(double x, double y)
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, x, y));
	}

	[Fact]
	public void Just_inside_the_edge_guard_counts()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.NewCard, Update(t, WindowDropTargets.EdgeGuard, 410));
		Assert.Equal(WindowDropTarget.NewCard, Update(t, 140, WindowDropTargets.EdgeGuard));
	}

	[Fact]
	public void The_edge_guard_is_on_the_right_edge_for_a_right_sidebar()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, 1915, 290, geometry: Right, cards: RightCards));
		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 1780, 290, geometry: Right, cards: RightCards));
		Assert.Equal(WindowDropTarget.NewCard, Update(t, 1780, 410, geometry: Right, cards: RightCards));
		Assert.Equal(WindowDropTarget.None, Update(t, 1500, 290, geometry: Right, cards: RightCards));
	}

	[Fact]
	public void Below_the_monitor_there_is_no_target()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.None, Update(t, 140, 1100));
	}

	[Fact]
	public void A_card_stays_the_target_near_its_edge()
	{
		var t = new WindowDropTargets();

		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 290));
		// within the leave margin below the card
		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 385));
		Assert.Equal(WindowDropTarget.NewCard, Update(t, 140, 410));
	}

	[Fact]
	public void Update_reports_only_changes()
	{
		var t = new WindowDropTargets();

		Assert.True(t.Update(140, 290, Left, Cards, true, true));
		Assert.False(t.Update(150, 300, Left, Cards, true, true));
		Assert.True(t.Update(140, 410, Left, Cards, true, true));
		Assert.False(t.Update(140, 420, Left, Cards, true, true));
	}

	[Fact]
	public void Escape_cancels_until_the_next_drag()
	{
		var t = new WindowDropTargets();
		Update(t, 140, 290);

		Assert.True(t.Cancel());
		Assert.True(t.IsCancelled);
		Assert.Equal(WindowDropTarget.None, Update(t, 140, 290));
		Assert.Equal(WindowDropTarget.None, Update(t, 140, 410));

		t.Reset();
		Assert.False(t.IsCancelled);
		Assert.Equal(WindowDropTarget.OnCard(A), Update(t, 140, 290));
	}

	[Fact]
	public void Cancel_without_a_target_reports_no_change()
	{
		var t = new WindowDropTargets();

		Assert.False(t.Cancel());
	}
}

public class WindowDragStartTests
{
	private static readonly Area Window = new(400, 200, 1400, 900);

	[Fact]
	public void The_title_bar_starts_a_drag()
	{
		Assert.True(WindowDragStart.IsTitleBarDrag(true, WindowDragStart.HTCAPTION, Window, 800, 215, 48));
	}

	[Fact]
	public void The_title_bar_counts_wherever_the_window_says_so()
	{
		// a window answers for itself, also far below the band
		Assert.True(WindowDragStart.IsTitleBarDrag(true, WindowDragStart.HTCAPTION, Window, 800, 500, 48));
	}

	[Theory]
	[InlineData(10)] // HTLEFT
	[InlineData(11)] // HTRIGHT
	[InlineData(12)] // HTTOP
	[InlineData(13)] // HTTOPLEFT
	[InlineData(14)] // HTTOPRIGHT
	[InlineData(15)] // HTBOTTOM
	[InlineData(16)] // HTBOTTOMLEFT
	[InlineData(17)] // HTBOTTOMRIGHT
	[InlineData(18)] // HTBORDER
	public void Resizing_at_an_edge_is_no_drag(int hitTest)
	{
		Assert.False(WindowDragStart.IsTitleBarDrag(true, hitTest, Window, 800, 201, 48));
	}

	[Fact]
	public void Moving_with_the_keyboard_is_no_drag()
	{
		Assert.False(WindowDragStart.IsTitleBarDrag(false, WindowDragStart.HTCAPTION, Window, 800, 215, 48));
	}

	[Fact]
	public void An_own_title_bar_in_the_client_area_counts_in_the_band_at_the_top()
	{
		Assert.True(WindowDragStart.IsTitleBarDrag(true, WindowDragStart.HTCLIENT, Window, 800, 230, 48));
		Assert.False(WindowDragStart.IsTitleBarDrag(true, WindowDragStart.HTCLIENT, Window, 800, 300, 48));
	}

	[Fact]
	public void A_window_that_does_not_answer_counts_in_the_band_at_the_top()
	{
		Assert.True(WindowDragStart.IsTitleBarDrag(true, null, Window, 800, 240, 48));
		Assert.False(WindowDragStart.IsTitleBarDrag(true, null, Window, 800, 260, 48));
		Assert.False(WindowDragStart.IsTitleBarDrag(true, null, Window, 300, 220, 48));
	}
}

public class WindowDragEndTests
{
	private static readonly Area Start = new(400, 200, 1400, 900);
	private static readonly Area Moved = new(-200, 150, 800, 850);

	[Fact]
	public void Letting_go_above_a_target_drops()
	{
		Assert.Equal(WindowDropOutcome.Drop, WindowDragEnd.Decide(WindowDropTarget.NewCard, false, Start, Moved));
		Assert.Equal(WindowDropOutcome.Drop, WindowDragEnd.Decide(WindowDropTarget.OnCard(Guid.NewGuid()), false, Start, Moved));
	}

	[Fact]
	public void Letting_go_without_a_target_does_nothing()
	{
		Assert.Equal(WindowDropOutcome.NoTarget, WindowDragEnd.Decide(WindowDropTarget.None, false, Start, Moved));
	}

	[Fact]
	public void Escape_during_the_drag_cancels()
	{
		Assert.Equal(WindowDropOutcome.Cancelled, WindowDragEnd.Decide(WindowDropTarget.NewCard, true, Start, Moved));
	}

	[Fact]
	public void A_window_Windows_put_back_where_it_started_was_cancelled()
	{
		// Escape or a right click too quick for the timer: the pointer may still be above a card
		Assert.Equal(WindowDropOutcome.Cancelled, WindowDragEnd.Decide(WindowDropTarget.NewCard, false, Start, Start));
		Assert.Equal(WindowDropOutcome.Cancelled, WindowDragEnd.Decide(WindowDropTarget.NewCard, false, Start, new Area(401, 199, 1402, 900)));
	}

	[Fact]
	public void A_few_pixels_more_are_a_real_move()
	{
		Assert.Equal(WindowDropOutcome.Drop, WindowDragEnd.Decide(WindowDropTarget.NewCard, false, Start, new Area(397, 200, 1397, 900)));
	}

	[Fact]
	public void A_maximized_window_that_was_restored_by_the_drag_moved()
	{
		var maximized = new Area(-8, -8, 1928, 1048);
		var restored = new Area(-300, 20, 900, 820);

		Assert.Equal(WindowDropOutcome.Drop, WindowDragEnd.Decide(WindowDropTarget.NewCard, false, maximized, restored));
	}
}

public class WindowDropRulesTests
{
	private static readonly WindowDropTarget Card = WindowDropTarget.OnCard(Guid.NewGuid());

	[Fact]
	public void With_other_windows_on_the_stage_only_the_window_leaves()
	{
		Assert.Equal(WindowDropPlan.OwnCard, WindowDropRules.Plan(WindowDropTarget.NewCard, 2));
		Assert.Equal(WindowDropPlan.IntoCard, WindowDropRules.Plan(Card, 3));
	}

	[Fact]
	public void A_window_alone_on_the_stage_takes_the_scene_of_the_card_onto_the_stage()
	{
		Assert.Equal(WindowDropPlan.IntoCardAndSwitch, WindowDropRules.Plan(Card, 1));
	}

	[Fact]
	public void A_window_alone_on_the_stage_gets_no_card_of_its_own()
	{
		Assert.Equal(WindowDropPlan.Ignore, WindowDropRules.Plan(WindowDropTarget.NewCard, 1));
	}

	[Fact]
	public void No_target_or_the_own_scene_does_nothing()
	{
		Assert.Equal(WindowDropPlan.Ignore, WindowDropRules.Plan(WindowDropTarget.None, 2));
		Assert.Equal(WindowDropPlan.Ignore, WindowDropRules.Plan(Card, 2, targetIsStage: true));
		Assert.Equal(WindowDropPlan.Ignore, WindowDropRules.Plan(new WindowDropTarget(WindowDropKind.Card), 2));
		Assert.Equal(WindowDropPlan.Ignore, WindowDropRules.Plan(Card, 0));
	}
}

public class WindowDragPresenceTests
{
	private static readonly SidebarGeometry Left = new(new Area(0, 0, 1920, 1040), 280, SidebarSide.Left);
	private static readonly SidebarGeometry Right = new(new Area(0, 0, 1920, 1040), 280, SidebarSide.Right);

	private static SidebarInput At(double x, bool windowDrag, double y = 500, double now = 10) =>
		new(x, y, Overlapped: true, Hold: false, now, WindowDrag: windowDrag);

	[Fact]
	public void A_dragged_window_brings_the_hidden_sidebar_back_anywhere_above_it()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.OffScreen, At(200, windowDrag: true)));
	}

	[Fact]
	public void Without_a_dragged_window_only_the_screen_edge_brings_it_back()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.OffScreen, p.Decide(SidebarMode.OffScreen, At(200, windowDrag: false)));
	}

	[Fact]
	public void It_stays_while_the_window_is_above_it_although_the_window_covers_it()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.Flyover, At(150, windowDrag: true, now: 10)));
		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.Flyover, At(150, windowDrag: true, now: 20)));
	}

	[Fact]
	public void It_hides_again_when_the_dragged_window_goes_back_to_the_stage()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.OffScreen, At(200, windowDrag: true, now: 10)));
		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.Flyover, At(900, windowDrag: true, now: 10.1)));
		Assert.Equal(SidebarMode.OffScreen, p.Decide(SidebarMode.Flyover, At(900, windowDrag: true, now: 11)));
	}

	[Fact]
	public void A_dragged_window_on_the_stage_does_not_bring_it_back()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.OffScreen, p.Decide(SidebarMode.OffScreen, At(900, windowDrag: true)));
	}

	[Fact]
	public void It_works_on_the_right_side()
	{
		var p = new SidebarPresence(Right);

		Assert.Equal(SidebarMode.Flyover, p.Decide(SidebarMode.OffScreen, At(1750, windowDrag: true)));
		Assert.Equal(SidebarMode.OffScreen, p.Decide(SidebarMode.OffScreen, At(200, windowDrag: true)));
	}

	[Fact]
	public void A_dragged_window_on_another_monitor_does_not_bring_it_back()
	{
		var p = new SidebarPresence(Left);

		Assert.Equal(SidebarMode.OffScreen, p.Decide(SidebarMode.OffScreen, At(200, windowDrag: true, y: 1500)));
	}
}

public class WindowDropSettingsTests
{
	[Fact]
	public void Dropping_windows_into_the_sidebar_is_on_by_default_and_survives_a_round_trip()
	{
		var s = new AppSettings().Normalize();
		Assert.True(s.Sidebar.DropWindowToCard);

		s.Sidebar.DropWindowToCard = false;
		Assert.False(AppSettings.Deserialize(s.Serialize())!.Sidebar.DropWindowToCard);
	}

	[Fact]
	public void Settings_without_the_switch_keep_it_on()
	{
		var s = AppSettings.Deserialize("{\"Sidebar\":{\"DragToStage\":false}}")!;

		Assert.True(s.Sidebar.DropWindowToCard);
	}
}
