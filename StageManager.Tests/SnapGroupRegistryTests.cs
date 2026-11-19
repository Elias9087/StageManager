using StageManager.Core;

namespace StageManager.Tests;

public class SnapGroupRegistryTests
{
	private const long Monitor = 65537;

	// windows 1 and 2 belong to app A, 3 to app B, 4 to app C
	private static SceneRegistry Create(out IReadOnlyList<SceneGroupSnapshot> groups)
	{
		var registry = new SceneRegistry();
		groups = registry.Initialize(new[] { (1L, "A"), (2L, "A"), (3L, "B"), (4L, "C") });
		return registry;
	}

	[Fact]
	public void A_snap_group_takes_the_windows_out_of_their_groups()
	{
		var registry = Create(out var groups);

		var result = registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor);

		Assert.NotNull(result);
		var id = result!.Value.GroupId;
		Assert.Equal(GroupKind.SnapGroup, registry.KindOf(id));
		Assert.Equal(Monitor, registry.MonitorOf(id));
		Assert.Equal(new long[] { 2, 3 }, registry.WindowsOf(id));
		Assert.Equal(new[] { "A", "B" }, registry.KeysOf(id));

		// A keeps window 1, B is empty and gone
		Assert.Equal(new long[] { 1 }, registry.WindowsOf(groups[0].Id));
		Assert.Equal(new[] { groups[1].Id }, result.Value.RemovedGroups);
		Assert.DoesNotContain(groups[1].Id, registry.GroupIds);
	}

	[Fact]
	public void The_snap_group_takes_the_place_of_the_group_of_its_first_window()
	{
		var registry = Create(out var groups);

		var id = registry.CreateSnapGroup(new long[] { 3, 4 }, Monitor)!.Value.GroupId;

		Assert.Equal(new[] { groups[0].Id, id }, registry.GroupIds);
	}

	[Fact]
	public void A_snap_group_needs_two_known_windows()
	{
		var registry = Create(out _);

		Assert.Null(registry.CreateSnapGroup(new long[] { 3 }, Monitor));
		Assert.Null(registry.CreateSnapGroup(new long[] { 3, 99 }, Monitor));
		Assert.Null(registry.CreateSnapGroup(new long[] { 3, 3 }, Monitor));
	}

	[Fact]
	public void Groups_of_apps_have_no_monitor()
	{
		var registry = Create(out var groups);

		Assert.Equal(GroupKind.Apps, registry.KindOf(groups[0].Id));
		Assert.Equal(0, registry.MonitorOf(groups[0].Id));
		Assert.Null(registry.KindOf(Guid.NewGuid()));
	}

	[Fact]
	public void A_new_window_never_joins_a_snap_group_but_opens_a_group_of_its_app()
	{
		var registry = Create(out _);
		var snap = registry.CreateSnapGroup(new long[] { 3, 4 }, Monitor)!.Value.GroupId;

		var placement = registry.Place(5, "B");

		Assert.True(placement.Created);
		Assert.NotEqual(snap, placement.GroupId);
		Assert.Equal(new long[] { 3, 4 }, registry.WindowsOf(snap));
	}

	[Fact]
	public void A_new_window_joins_the_group_of_its_app_even_if_another_window_of_the_app_is_in_a_snap_group()
	{
		// two KiTTY windows: one is snapped next to Explorer, the next new one still goes to the KiTTY card
		var registry = Create(out var groups);
		registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor);

		var placement = registry.Place(5, "A");

		Assert.False(placement.Created);
		Assert.Equal(groups[0].Id, placement.GroupId);
	}

	[Fact]
	public void A_window_returns_to_the_group_of_its_app()
	{
		var registry = Create(out var groups);
		var snap = registry.CreateSnapGroup(new long[] { 2, 3, 4 }, Monitor)!.Value.GroupId;

		var home = registry.ReturnHome(2);

		Assert.NotNull(home);
		Assert.Equal(snap, home!.Value.SourceId);
		Assert.False(home.Value.SourceRemoved);
		Assert.Equal(groups[0].Id, home.Value.TargetId);
		Assert.False(home.Value.TargetCreated);
		Assert.Equal(new long[] { 1, 2 }, registry.WindowsOf(groups[0].Id));
		Assert.Equal(new[] { "B", "C" }, registry.KeysOf(snap));
	}

	[Fact]
	public void A_window_whose_app_has_no_group_gets_a_new_one_right_after_the_snap_group()
	{
		var registry = Create(out var groups);
		var snap = registry.CreateSnapGroup(new long[] { 3, 4 }, Monitor)!.Value.GroupId;

		var home = registry.ReturnHome(3)!.Value;

		Assert.True(home.TargetCreated);
		Assert.Equal(new[] { groups[0].Id, snap, home.TargetId }, registry.GroupIds);
		Assert.Equal(new[] { "B" }, registry.KeysOf(home.TargetId));
	}

	[Fact]
	public void A_window_outside_of_a_snap_group_is_home_already()
	{
		var registry = Create(out _);

		Assert.Null(registry.ReturnHome(1));
		Assert.Null(registry.ReturnHome(99));
	}

	[Fact]
	public void A_snap_group_with_one_window_left_is_dissolved()
	{
		var registry = Create(out var groups);
		var snap = registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor)!.Value.GroupId;
		registry.Remove(3);

		var results = registry.DissolveIfTooSmall(snap);

		var home = Assert.Single(results);
		Assert.True(home.SourceRemoved);
		Assert.Equal(groups[0].Id, home.TargetId);
		Assert.DoesNotContain(snap, registry.GroupIds);
	}

	[Fact]
	public void A_snap_group_with_two_windows_stays()
	{
		var registry = Create(out _);
		var snap = registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor)!.Value.GroupId;

		Assert.Empty(registry.DissolveIfTooSmall(snap));
		Assert.Contains(snap, registry.GroupIds);
	}

	[Fact]
	public void A_group_of_apps_is_never_dissolved()
	{
		var registry = Create(out var groups);

		Assert.Empty(registry.DissolveIfTooSmall(groups[2].Id));
	}

	[Fact]
	public void A_card_merged_into_a_snap_group_keeps_it_a_snap_group()
	{
		var registry = Create(out var groups);
		var snap = registry.CreateSnapGroup(new long[] { 2, 3 }, Monitor)!.Value.GroupId;

		registry.Merge(groups[2].Id, snap);

		Assert.Equal(GroupKind.SnapGroup, registry.KindOf(snap));
		Assert.Equal(new long[] { 2, 3, 4 }, registry.WindowsOf(snap));
	}

	[Fact]
	public void A_snap_group_merged_into_a_card_of_apps_becomes_part_of_it()
	{
		var registry = Create(out var groups);
		var snap = registry.CreateSnapGroup(new long[] { 3, 4 }, Monitor)!.Value.GroupId;

		registry.Merge(snap, groups[0].Id);

		Assert.Equal(GroupKind.Apps, registry.KindOf(groups[0].Id));
		Assert.Equal(new long[] { 1, 2, 3, 4 }, registry.WindowsOf(groups[0].Id));
	}
}
