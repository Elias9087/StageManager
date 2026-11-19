using StageManager.Core;

namespace StageManager.Tests;

public class DetachTests
{
	private const long Monitor = 65537;

	// windows 1 and 2 are two KiTTY windows, 3 is Explorer
	private static SceneRegistry Create(out IReadOnlyList<SceneGroupSnapshot> groups)
	{
		var registry = new SceneRegistry();
		groups = registry.Initialize(new[] { (1L, "KITTY"), (2L, "KITTY"), (3L, "EXPLORER") });
		return registry;
	}

	[Fact]
	public void A_window_of_two_of_the_same_app_gets_a_group_of_its_own()
	{
		var registry = Create(out var groups);

		var result = registry.Detach(2);

		Assert.NotNull(result);
		Assert.Equal(groups[0].Id, result!.Value.SourceId);
		Assert.Equal(GroupKind.Window, registry.KindOf(result.Value.GroupId));
		Assert.Equal(new long[] { 2 }, registry.WindowsOf(result.Value.GroupId));
		Assert.Equal(new[] { "KITTY" }, registry.KeysOf(result.Value.GroupId));
		Assert.Equal(new long[] { 1 }, registry.WindowsOf(groups[0].Id));
		Assert.Equal(new[] { "KITTY" }, registry.KeysOf(groups[0].Id));
		Assert.True(registry.IsDetached(2));
	}

	[Fact]
	public void The_new_group_comes_right_after_the_one_the_window_left()
	{
		var registry = Create(out var groups);

		var id = registry.Detach(1)!.Value.GroupId;

		Assert.Equal(new[] { groups[0].Id, id, groups[1].Id }, registry.GroupIds);
	}

	[Fact]
	public void A_window_alone_in_its_group_is_not_detached()
	{
		var registry = Create(out _);

		Assert.Null(registry.Detach(3));
		Assert.Null(registry.Detach(99));
		Assert.False(registry.IsDetached(3));
	}

	[Fact]
	public void New_windows_of_the_app_still_go_to_the_group_of_the_app()
	{
		var registry = Create(out var groups);
		registry.Detach(2);

		var placement = registry.Place(4, "KITTY");

		Assert.False(placement.Created);
		Assert.Equal(groups[0].Id, placement.GroupId);
	}

	[Fact]
	public void If_the_group_of_the_app_is_gone_a_new_window_opens_one_and_does_not_join_the_detached_window()
	{
		var registry = Create(out _);
		var detached = registry.Detach(2)!.Value.GroupId;
		registry.Remove(1);

		var placement = registry.Place(4, "KITTY");

		Assert.True(placement.Created);
		Assert.NotEqual(detached, placement.GroupId);
		Assert.Equal(GroupKind.Apps, registry.KindOf(placement.GroupId));
	}

	[Fact]
	public void A_detached_window_leaving_a_snap_group_gets_a_group_of_its_own_again()
	{
		var registry = Create(out var groups);
		registry.Detach(2);
		var snap = registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor)!.Value.GroupId;

		var home = registry.ReturnHome(2)!.Value;

		Assert.True(home.TargetCreated);
		Assert.NotEqual(groups[0].Id, home.TargetId);
		Assert.Equal(GroupKind.Window, registry.KindOf(home.TargetId));
		Assert.Equal(new long[] { 1 }, registry.WindowsOf(groups[0].Id));
		Assert.Contains(snap, registry.GroupIds);
	}

	[Fact]
	public void A_window_that_was_not_detached_leaving_a_snap_group_goes_to_the_group_of_its_app()
	{
		var registry = Create(out var groups);
		registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor);

		var home = registry.ReturnHome(2)!.Value;

		Assert.Equal(groups[0].Id, home.TargetId);
	}

	[Fact]
	public void A_window_can_be_detached_from_a_snap_group()
	{
		var registry = new SceneRegistry();
		registry.Initialize(new[] { (1L, "A"), (2L, "B"), (3L, "C") });
		var snap = registry.CreateSnapGroup(new long[] { 1, 2, 3 }, Monitor)!.Value.GroupId;

		var result = registry.Detach(3)!.Value;

		Assert.Equal(snap, result.SourceId);
		Assert.Equal(new long[] { 1, 2 }, registry.WindowsOf(snap));
		Assert.Equal(new[] { "A", "B" }, registry.KeysOf(snap));
	}

	[Fact]
	public void A_closed_window_is_forgotten_as_detached()
	{
		var registry = Create(out _);
		registry.Detach(2);

		registry.Remove(2);

		Assert.False(registry.IsDetached(2));
	}

	[Fact]
	public void The_card_of_an_app_merged_into_a_detached_window_makes_it_a_card_of_apps()
	{
		var registry = Create(out var groups);
		var detached = registry.Detach(2)!.Value.GroupId;

		registry.Merge(groups[1].Id, detached);

		Assert.Equal(GroupKind.Apps, registry.KindOf(detached));
		Assert.Equal(detached, registry.Place(5, "EXPLORER").GroupId);
	}

	[Fact]
	public void A_detached_window_merged_into_another_card_joins_it()
	{
		var registry = Create(out var groups);
		var detached = registry.Detach(2)!.Value.GroupId;

		registry.Merge(detached, groups[1].Id);

		Assert.Equal(GroupKind.Apps, registry.KindOf(groups[1].Id));
		Assert.Equal(new long[] { 3, 2 }, registry.WindowsOf(groups[1].Id));
	}
}
