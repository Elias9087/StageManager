using StageManager.Core;

namespace StageManager.Tests;

public class FocusHandoffTests
{
	private DateTime _now = new(2026, 11, 19, 14, 50, 27, DateTimeKind.Utc);

	private FocusHandoff Create() => new(() => _now);

	private void Advance(int milliseconds) => _now = _now.AddMilliseconds(milliseconds);

	[Fact]
	public void A_parked_window_that_gets_the_focus_right_after_the_active_window_closed_is_a_handoff()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);
		Advance(17);

		Assert.True(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void The_handoff_ends_after_the_time_window()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);
		Advance(301);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void The_time_window_can_be_set()
	{
		var focus = new FocusHandoff(() => _now, TimeSpan.FromMilliseconds(100));
		focus.Activated(1);
		focus.Vanished(1);
		Advance(150);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void Without_a_vanished_window_an_activation_is_the_user_e_g_a_click_on_the_taskbar()
	{
		var focus = Create();
		focus.Activated(1);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void Only_the_active_window_hands_the_focus_on()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(2);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
		Assert.Equal(1, focus.Active);
	}

	[Fact]
	public void A_window_on_the_screen_is_never_a_handoff()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);

		Assert.False(focus.IsHandoff(targetOnScreen: true, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void A_window_of_the_current_scene_is_never_a_handoff()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: true, currentSceneHasWindowOnScreen: true));
	}

	[Fact]
	public void If_the_current_scene_has_no_window_left_the_switch_happens()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);

		Assert.False(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: false));
	}

	[Fact]
	public void A_vanished_window_is_no_longer_active()
	{
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);

		Assert.Null(focus.Active);
	}

	[Fact]
	public void A_new_activation_does_not_end_the_time_window()
	{
		// Windows may hand the focus on twice in a row, e.g. first to a parked window and right after to another one
		var focus = Create();
		focus.Activated(1);
		focus.Vanished(1);
		focus.Activated(2);
		Advance(50);

		Assert.True(focus.IsHandoff(targetOnScreen: false, targetInCurrentScene: false, currentSceneHasWindowOnScreen: true));
	}

	[Theory]
	[InlineData(false, false, true, true)]
	[InlineData(true, false, true, false)]
	[InlineData(false, true, true, false)]
	[InlineData(false, false, false, false)]
	public void The_focus_is_stranded_only_on_a_window_out_of_sight_and_out_of_the_scene(bool onScreen, bool inScene, bool sceneHasWindow, bool expected)
	{
		Assert.Equal(expected, FocusHandoff.IsStranded(onScreen, inScene, sceneHasWindow));
	}
}
