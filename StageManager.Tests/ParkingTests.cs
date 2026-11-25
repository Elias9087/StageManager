using StageManager.Core;

namespace StageManager.Tests;

public class ParkingSpotTests
{
	private static readonly Area Primary = new(0, 0, 1920, 1080);

	[Fact]
	public void The_window_is_parked_beyond_the_left_edge_of_a_single_monitor()
	{
		var spot = ParkingSpot.Find(new Area(300, 200, 1100, 700), new[] { Primary });

		Assert.NotNull(spot);
		Assert.Equal(new Area(-960, 200, -160, 700), spot);
	}

	[Fact]
	public void The_parked_window_keeps_its_size()
	{
		var window = new Area(300, 200, 1100, 700);

		var spot = ParkingSpot.Find(window, new[] { Primary })!.Value;

		Assert.Equal(window.Width, spot.Width);
		Assert.Equal(window.Height, spot.Height);
	}

	[Fact]
	public void The_parked_window_does_not_touch_any_monitor()
	{
		var monitors = new[] { Primary, new Area(1920, 0, 3840, 1080) };

		var spot = ParkingSpot.Find(new Area(300, 200, 1100, 700), monitors)!.Value;

		Assert.All(monitors, m => Assert.False(FlightPlanner.Intersects(m, spot)));
	}

	[Fact]
	public void Windows_stay_at_the_monitor_they_are_on()
	{
		var left = new Area(0, 0, 1920, 1080);
		var right = new Area(1920, 0, 3840, 1080);
		var monitors = new[] { left, right };
		var window = new Area(2400, 200, 3200, 700);

		var spot = ParkingSpot.Find(window, monitors)!.Value;

		Assert.Equal(1, ParkingSpot.NearestMonitor(spot, monitors));
	}

	[Fact]
	public void A_window_on_the_left_of_two_monitors_is_parked_on_the_left_side()
	{
		var monitors = new[] { new Area(0, 0, 1920, 1080), new Area(1920, 0, 3840, 1080) };

		var spot = ParkingSpot.Find(new Area(300, 200, 1100, 700), monitors)!.Value;

		Assert.True(spot.Right < 0);
		Assert.Equal(0, ParkingSpot.NearestMonitor(spot, monitors));
	}

	[Fact]
	public void Monitors_above_each_other_park_on_the_free_side()
	{
		var top = new Area(0, -1080, 1920, 0);
		var bottom = new Area(0, 0, 1920, 1080);
		var monitors = new[] { top, bottom };

		var spot = ParkingSpot.Find(new Area(300, 200, 1100, 700), monitors)!.Value;

		Assert.All(monitors, m => Assert.False(FlightPlanner.Intersects(m, spot)));
		Assert.Equal(1, ParkingSpot.NearestMonitor(spot, monitors));
	}

	[Fact]
	public void A_monitor_with_a_negative_origin_is_handled()
	{
		var monitors = new[] { new Area(-1920, 0, 0, 1080), new Area(0, 0, 1920, 1080) };

		var spot = ParkingSpot.Find(new Area(-1500, 100, -700, 600), monitors)!.Value;

		Assert.True(spot.Right < -1920);
		Assert.Equal(0, ParkingSpot.NearestMonitor(spot, monitors));
	}

	[Fact]
	public void Nothing_is_found_without_monitors_or_for_an_empty_window()
	{
		Assert.Null(ParkingSpot.Find(new Area(0, 0, 100, 100), Array.Empty<Area>()));
		Assert.Null(ParkingSpot.Find(new Area(0, 0, 0, 0), new[] { Primary }));
	}

	[Fact]
	public void The_nearest_monitor_is_the_one_with_the_largest_overlap()
	{
		var monitors = new[] { new Area(0, 0, 1000, 1000), new Area(1000, 0, 2000, 1000) };

		Assert.Equal(0, ParkingSpot.NearestMonitor(new Area(800, 0, 1100, 100), monitors));
		Assert.Equal(1, ParkingSpot.NearestMonitor(new Area(900, 0, 1500, 100), monitors));
	}

	[Fact]
	public void A_rectangle_outside_everything_belongs_to_the_closest_monitor()
	{
		var monitors = new[] { new Area(0, 0, 1000, 1000), new Area(1000, 0, 2000, 1000) };

		Assert.Equal(0, ParkingSpot.NearestMonitor(new Area(-500, 0, -100, 100), monitors));
		Assert.Equal(1, ParkingSpot.NearestMonitor(new Area(2100, 0, 2500, 100), monitors));
	}

	[Fact]
	public void With_monitors_on_the_left_and_the_right_a_window_of_the_middle_one_is_parked_below_and_touches_no_monitor()
	{
		var monitors = new[] { new Area(0, 0, 1920, 1080), new Area(-2560, 0, 0, 1440), new Area(1920, 0, 3840, 1080) };
		var window = new Area(300, 200, 1100, 700);

		var spot = ParkingSpot.Find(window, monitors);

		Assert.NotNull(spot);
		Assert.DoesNotContain(monitors, m => FlightPlanner.Intersects(m, spot!.Value));
		Assert.Equal(0, ParkingSpot.NearestMonitor(spot!.Value, monitors));
	}
}

public class WindowParkerTests
{
	private sealed class FakePlacer : IWindowPlacer
	{
		public Dictionary<long, (int Process, Area Rect, bool Minimized)> Windows { get; } = new();

		public Dictionary<long, List<long>> Owned { get; } = new();

		public List<Area> MonitorList { get; } = new() { new Area(0, 0, 1920, 1080) };

		public List<(long Window, Area Rect, bool Refresh)> Moves { get; } = new();

		public bool Exists(long window, int processId) => Windows.TryGetValue(window, out var w) && w.Process == processId;

		public int ProcessIdOf(long window) => Windows.TryGetValue(window, out var w) ? w.Process : 0;

		public Area? GetRect(long window) => Windows.TryGetValue(window, out var w) ? w.Rect : null;

		// the visible frame is the rectangle without 7 invisible pixels at the sides
		public Area? GetFrame(long window) => GetRect(window) is Area r ? new Area(r.Left + 7, r.Top, r.Right - 7, r.Bottom - 7) : null;

		public bool IsMinimized(long window) => Windows.TryGetValue(window, out var w) && w.Minimized;

		public HashSet<long> MaximizedWindows { get; } = new();

		public Dictionary<long, Area> RestoredRects { get; } = new();

		public bool Hung { get; set; }

		public bool IsMaximized(long window) => MaximizedWindows.Contains(window);

		public Area? GetRestoredRect(long window) => RestoredRects.TryGetValue(window, out var r) ? r : null;

		public bool Normalize(long window, Area rect)
		{
			if (Hung || !Windows.ContainsKey(window))
				return false;

			// a snapped window that was minimized comes back where it was snapped, whatever the placement says
			var at = Windows[window].Minimized && RestoresAt.TryGetValue(window, out var snapped) ? snapped : rect;

			MaximizedWindows.Remove(window);
			Windows[window] = Windows[window] with { Rect = at, Minimized = false };
			Normalizes.Add((window, rect));
			return true;
		}

		public List<(long Window, Area Restored)> Maximizes { get; } = new();

		public List<(long Window, Area Rect)> Normalizes { get; } = new();

		public Dictionary<long, Area> RestoresAt { get; } = new();

		public List<(long Window, Area Rect, bool Maximized)> Places { get; } = new();

		public bool Place(long window, Area rect, bool maximized)
		{
			if (Hung || !Windows.ContainsKey(window))
				return false;

			Places.Add((window, rect, maximized));
			if (maximized)
			{
				MaximizedWindows.Add(window);
				RestoredRects[window] = rect;
			}
			else
				MaximizedWindows.Remove(window);

			Windows[window] = Windows[window] with { Rect = rect, Minimized = false };
			return true;
		}

		public bool Maximize(long window, Area restored)
		{
			if (Hung || !Windows.ContainsKey(window))
				return false;

			MaximizedWindows.Add(window);
			RestoredRects[window] = restored;
			Windows[window] = Windows[window] with { Rect = MaximizedRect, Minimized = false };
			Maximizes.Add((window, restored));
			return true;
		}

		public static readonly Area MaximizedRect = new(-8, -8, 1928, 1048);

		// Windows puts a maximized window back where it was
		public bool Move(long window, Area rect, bool refreshFrame)
		{
			if (!Windows.TryGetValue(window, out var w))
				return false;

			Moves.Add((window, rect, refreshFrame));
			if (!MaximizedWindows.Contains(window))
				Windows[window] = w with { Rect = rect };

			return true;
		}

		public IReadOnlyList<long> OwnedWindows(long window) => Owned.TryGetValue(window, out var list) ? list : new List<long>();

		public IReadOnlyList<Area> Monitors() => MonitorList;
	}

	private sealed class MemoryJournal : IParkingJournal
	{
		public List<ParkedWindow> Entries { get; private set; } = new();

		public int Saves { get; private set; }

		public IReadOnlyList<ParkedWindow> Load() => Entries.ToList();

		public void Save(IReadOnlyList<ParkedWindow> entries)
		{
			Entries = entries.ToList();
			Saves++;
		}
	}

	private static readonly Area Rect = new(300, 200, 1100, 700);

	private readonly FakePlacer _placer = new();
	private readonly MemoryJournal _journal = new();

	private WindowParker Create()
	{
		_placer.Windows[1] = (100, Rect, false);
		return new WindowParker(_placer, _journal);
	}

	[Fact]
	public void Park_moves_the_window_out_of_the_screen_and_remembers_it()
	{
		var p = Create();

		Assert.True(p.Park(1));

		Assert.True(p.IsParked(1));
		Assert.True(_placer.Windows[1].Rect.Right < 0);
		Assert.Single(_journal.Entries);
		Assert.Equal(Rect, _journal.Entries[0].Original);
	}

	[Fact]
	public void The_journal_is_written_before_the_window_moves()
	{
		var p = Create();
		var journalHadItWhenMoved = false;
		var placer = new MovingPlacer(_placer, () => journalHadItWhenMoved = _journal.Entries.Count == 1);
		var parker = new WindowParker(placer, _journal);

		parker.Park(1);

		Assert.True(journalHadItWhenMoved);
	}

	private sealed class MovingPlacer : IWindowPlacer
	{
		private readonly FakePlacer _inner;
		private readonly Action _onMove;

		public MovingPlacer(FakePlacer inner, Action onMove)
		{
			_inner = inner;
			_onMove = onMove;
		}

		public bool Exists(long window, int processId) => _inner.Exists(window, processId);
		public int ProcessIdOf(long window) => _inner.ProcessIdOf(window);
		public Area? GetRect(long window) => _inner.GetRect(window);
		public Area? GetFrame(long window) => _inner.GetFrame(window);
		public bool IsMinimized(long window) => _inner.IsMinimized(window);
		public bool IsMaximized(long window) => _inner.IsMaximized(window);
		public Area? GetRestoredRect(long window) => _inner.GetRestoredRect(window);
		public bool Normalize(long window, Area rect) => _inner.Normalize(window, rect);
		public bool Maximize(long window, Area restored) => _inner.Maximize(window, restored);
		public bool Place(long window, Area rect, bool maximized) => _inner.Place(window, rect, maximized);
		public IReadOnlyList<long> OwnedWindows(long window) => _inner.OwnedWindows(window);
		public IReadOnlyList<Area> Monitors() => _inner.Monitors();

		public bool Move(long window, Area rect, bool refreshFrame)
		{
			_onMove();
			return _inner.Move(window, rect, refreshFrame);
		}
	}

	[Fact]
	public void Unpark_brings_the_window_back_and_refreshes_its_frame()
	{
		var p = Create();
		p.Park(1);

		Assert.True(p.Unpark(1));

		Assert.Equal(Rect, _placer.Windows[1].Rect);
		Assert.True(_placer.Moves[^1].Refresh);
		Assert.False(p.IsParked(1));
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void Parking_does_not_ask_for_a_frame_refresh()
	{
		var p = Create();

		p.Park(1);

		Assert.False(_placer.Moves[0].Refresh);
	}

	[Fact]
	public void Parking_twice_moves_only_once()
	{
		var p = Create();

		p.Park(1);
		p.Park(1);

		Assert.Single(_placer.Moves);
		Assert.Single(_journal.Entries);
	}

	[Fact]
	public void A_minimized_window_is_not_parked()
	{
		var p = Create();
		_placer.Windows[1] = (100, Rect, true);

		Assert.False(p.Park(1));

		Assert.Empty(_placer.Moves);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void An_unknown_window_is_not_parked()
	{
		Assert.False(Create().Park(99));
	}

	private WindowParker CreateMaximized(Area restored)
	{
		var p = Create();
		_placer.Windows[1] = (100, FakePlacer.MaximizedRect, false);
		_placer.MaximizedWindows.Add(1);
		_placer.RestoredRects[1] = restored;
		return p;
	}

	[Fact]
	public void A_maximized_window_leaves_the_maximized_state_and_really_leaves_the_screen()
	{
		var p = CreateMaximized(Rect);

		Assert.True(p.Park(1));

		Assert.False(_placer.IsMaximized(1));
		Assert.True(_placer.Windows[1].Rect.Right < 0);
		Assert.Equal(FakePlacer.MaximizedRect.Width, _placer.Windows[1].Rect.Width);
		Assert.True(_journal.Entries[0].Maximized);
		Assert.Equal(Rect, _journal.Entries[0].Restored);
	}

	[Fact]
	public void A_maximized_window_comes_back_maximized_and_keeps_its_restored_rectangle()
	{
		var p = CreateMaximized(Rect);
		p.Park(1);

		Assert.True(p.Unpark(1));

		Assert.True(_placer.IsMaximized(1));
		Assert.Equal(FakePlacer.MaximizedRect, _placer.Windows[1].Rect);
		Assert.Equal((1L, Rect), _placer.Maximizes[^1]);
	}

	[Fact]
	public void A_normal_window_is_parked_the_same_way_if_maximized_windows_are_moved_as_they_are()
	{
		Create();
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);

		Assert.True(p.Park(1));
		Assert.True(_placer.Windows[1].Rect.Right < 0);
		Assert.Equal(Rect.Width, _placer.Windows[1].Rect.Width);
	}

	[Fact]
	public void A_maximized_window_can_be_moved_without_leaving_the_maximized_state()
	{
		CreateMaximized(Rect);
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);

		Assert.True(p.Park(1));

		Assert.True(_placer.IsMaximized(1));
		Assert.True(_placer.Moves[^1].Rect.Right < 0);
		Assert.False(_journal.Entries[0].Maximized);
		Assert.True(_journal.Entries[0].MovedMaximized);
		Assert.Equal(Rect, _journal.Entries[0].Restored);
	}

	[Fact]
	public void A_maximized_window_parked_as_it_is_remembers_its_place_and_its_parking_spot()
	{
		// the check for windows that refuse to move compares the window with these two
		CreateMaximized(Rect);
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);
		p.Park(1);

		Assert.Equal(FakePlacer.MaximizedRect, p.OriginalRectOf(1));
		Assert.Equal(_placer.Moves[^1].Rect, p.ParkedRectOf(1));
		Assert.True(p.ParkedRectOf(1)!.Value.Right < 0);
		Assert.False(Elevation.RefusedToMove(p.OriginalRectOf(1)!.Value, p.ParkedRectOf(1)!.Value, p.ParkedRectOf(1)!.Value, hung: false));
	}

	[Fact]
	public void A_window_that_is_not_parked_has_no_parking_spot()
	{
		var p = Create();

		Assert.Null(p.ParkedRectOf(1));
	}

	[Fact]
	public void A_maximized_window_that_was_moved_as_it_is_is_moved_back_and_not_maximized_again()
	{
		CreateMaximized(Rect);
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);
		p.Park(1);

		// the fake keeps maximized windows in place, as if Windows had moved it out: put it out of the screen for the return
		_placer.Windows[1] = _placer.Windows[1] with { Rect = _placer.Moves[^1].Rect };

		Assert.True(p.Unpark(1));

		Assert.Empty(_placer.Maximizes);
		Assert.Equal(FakePlacer.MaximizedRect, _placer.Moves[^1].Rect);
	}

	[Fact]
	public void A_maximized_window_a_killed_run_left_behind_comes_back_maximized()
	{
		CreateMaximized(Rect).Park(1);

		new WindowParker(_placer, _journal).Recover();

		Assert.True(_placer.IsMaximized(1));
		Assert.Equal(FakePlacer.MaximizedRect, _placer.Windows[1].Rect);
	}

	[Fact]
	public void A_maximized_window_that_cannot_leave_its_state_is_not_parked()
	{
		var p = CreateMaximized(Rect);
		_placer.Hung = true;

		Assert.False(p.Park(1));

		Assert.False(p.IsParked(1));
		Assert.Empty(_journal.Entries);
		Assert.Empty(_placer.Moves);
	}

	[Fact]
	public void A_maximized_window_without_a_restored_rectangle_is_not_parked()
	{
		var p = CreateMaximized(Rect);
		_placer.RestoredRects.Clear();

		Assert.False(p.Park(1));
		Assert.True(_placer.IsMaximized(1));
	}

	[Fact]
	public void Without_a_free_place_the_window_is_not_parked()
	{
		var p = Create();
		_placer.MonitorList.Clear();

		Assert.False(p.Park(1));
	}

	[Fact]
	public void Owned_windows_follow_their_owner_in_both_directions()
	{
		var p = Create();
		var dialog = new Area(500, 300, 900, 500);
		_placer.Windows[2] = (100, dialog, false);
		_placer.Owned[1] = new List<long> { 2 };

		p.Park(1);
		var dx = _placer.Windows[1].Rect.Left - Rect.Left;
		Assert.Equal(new Area(dialog.Left + dx, dialog.Top, dialog.Right + dx, dialog.Bottom), _placer.Windows[2].Rect);
		Assert.Equal(2, _journal.Entries.Count);

		p.Unpark(1);
		Assert.Equal(Rect, _placer.Windows[1].Rect);
		Assert.Equal(dialog, _placer.Windows[2].Rect);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void RestoreAll_brings_every_window_back()
	{
		var p = Create();
		_placer.Windows[2] = (101, new Area(10, 10, 400, 300), false);
		p.Park(1);
		p.Park(2);

		var restored = p.RestoreAll();

		Assert.Equal(2, restored);
		Assert.Equal(Rect, _placer.Windows[1].Rect);
		Assert.Equal(new Area(10, 10, 400, 300), _placer.Windows[2].Rect);
		Assert.Equal(0, p.Count);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void Recover_brings_back_windows_a_killed_run_left_behind()
	{
		var first = Create();
		first.Park(1);
		var afterCrash = new WindowParker(_placer, _journal);

		var restored = afterCrash.Recover();

		Assert.Equal(1, restored);
		Assert.Equal(Rect, _placer.Windows[1].Rect);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void Recover_ignores_a_handle_that_belongs_to_another_window_now()
	{
		var first = Create();
		first.Park(1);
		// the window is gone and another process got the same handle
		_placer.Windows[1] = (555, new Area(50, 50, 400, 400), false);
		var afterCrash = new WindowParker(_placer, _journal);

		var restored = afterCrash.Recover();

		Assert.Equal(0, restored);
		Assert.Equal(new Area(50, 50, 400, 400), _placer.Windows[1].Rect);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void Recover_skips_windows_that_are_gone()
	{
		var first = Create();
		first.Park(1);
		_placer.Windows.Remove(1);

		Assert.Equal(0, new WindowParker(_placer, _journal).Recover());
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void A_window_that_came_back_on_its_own_stays_where_it_is()
	{
		var p = Create();
		p.Park(1);
		var userPlaced = new Area(700, 100, 1500, 600);
		_placer.Windows[1] = (100, userPlaced, false);

		p.Unpark(1);

		Assert.Equal(userPlaced, _placer.Windows[1].Rect);
	}

	[Fact]
	public void Forget_drops_a_window_without_moving_anything()
	{
		var p = Create();
		p.Park(1);
		var moves = _placer.Moves.Count;

		p.Forget(1);

		Assert.False(p.IsParked(1));
		Assert.Equal(moves, _placer.Moves.Count);
		Assert.Empty(_journal.Entries);
	}

	[Fact]
	public void The_original_frame_is_remembered_for_the_animation()
	{
		var p = Create();

		p.Park(1);

		Assert.Equal(new Area(307, 200, 1093, 693), p.OriginalFrameOf(1));
		Assert.Equal(Rect, p.OriginalRectOf(1));
		Assert.Null(p.OriginalRectOf(99));
	}

	[Fact]
	public void Unpark_of_a_window_that_is_not_parked_does_nothing()
	{
		Assert.False(Create().Unpark(1));
		Assert.Empty(_placer.Moves);
	}

	[Fact]
	public void Parked_windows_survive_a_round_trip_through_the_journal_file()
	{
		var path = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"), "parked.json");
		try
		{
			var journal = new FileParkingJournal(path);
			var entry = new ParkedWindow { Window = 42, ProcessId = 7, Owner = 3, Original = Rect, OriginalFrame = Rect, Parked = new Area(-960, 200, -160, 700) };

			journal.Save(new[] { entry });
			var loaded = journal.Load();

			Assert.Equal(entry, Assert.Single(loaded));

			journal.Save(Array.Empty<ParkedWindow>());
			Assert.Empty(journal.Load());
			Assert.False(File.Exists(path));
		}
		finally
		{
			var dir = Path.GetDirectoryName(path)!;
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void A_broken_journal_file_counts_as_empty()
	{
		var dir = Path.Combine(Path.GetTempPath(), "StageManagerTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, "parked.json");
		File.WriteAllText(path, "{ not json");

		try
		{
			Assert.Empty(new FileParkingJournal(path).Load());
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	// a second monitor left of the main one, as on 24.11.: -3440 to 0, reaching above the main monitor
	private static readonly Area LeftMonitor = new(-3440, -842, 0, 598);

	[Fact]
	public void With_a_monitor_on_the_left_a_window_of_the_main_monitor_is_parked_on_the_right_and_touches_no_monitor()
	{
		_placer.MonitorList.Add(LeftMonitor);
		var p = Create();

		Assert.True(p.Park(1));

		var parked = _placer.Windows[1].Rect;
		Assert.True(parked.Left >= 1920);
		Assert.DoesNotContain(_placer.MonitorList, m => FlightPlanner.Intersects(m, parked));
	}

	[Fact]
	public void A_monitor_plugged_in_where_windows_are_parked_makes_them_park_again()
	{
		var p = Create();
		p.Park(1);
		Assert.True(_placer.Windows[1].Rect.Right < 0);

		// the docking station brings the left monitor, the parked window would be seen on it
		_placer.MonitorList.Add(new Area(-1920, 0, 0, 1080));

		Assert.Equal(1, p.Repark());

		var parked = _placer.Windows[1].Rect;
		Assert.DoesNotContain(_placer.MonitorList, m => FlightPlanner.Intersects(m, parked));
		Assert.Equal(parked, p.ParkedRectOf(1));
		Assert.Equal(Rect, p.OriginalRectOf(1));
	}

	[Fact]
	public void Parked_windows_that_stay_out_of_the_screen_are_left_alone()
	{
		var p = Create();
		p.Park(1);
		var moves = _placer.Moves.Count;

		Assert.Equal(0, p.Repark());
		Assert.Equal(moves, _placer.Moves.Count);
	}

	[Fact]
	public void A_window_whose_monitor_was_unplugged_comes_back_to_a_monitor_that_exists()
	{
		_placer.MonitorList.Add(new Area(-1920, 0, 0, 1080));
		_placer.Windows[1] = (100, new Area(-1500, 200, -700, 700), false);
		var p = new WindowParker(_placer, _journal);
		p.Park(1);

		_placer.MonitorList.RemoveAt(1);
		p.Repark();
		p.Unpark(1);

		var back = _placer.Windows[1].Rect;
		Assert.True(FlightPlanner.Intersects(_placer.MonitorList[0], back));
		Assert.Equal(800, back.Width);
	}

	[Fact]
	public void A_window_still_at_its_parking_spot_on_a_new_monitor_comes_back_to_where_it_was()
	{
		var p = Create();
		p.Park(1);
		_placer.MonitorList.Add(new Area(-1920, 0, 0, 1080));

		Assert.True(p.Unpark(1));

		Assert.Equal(Rect, _placer.Windows[1].Rect);
	}

	[Fact]
	public void A_window_found_on_a_monitor_right_after_it_was_parked_can_be_parked_again()
	{
		var p = Create();
		p.Park(1);
		_placer.MonitorList.Add(new Area(-1920, 0, 0, 1080));

		Assert.True(p.Repark(1));
		Assert.DoesNotContain(_placer.MonitorList, m => FlightPlanner.Intersects(m, _placer.Windows[1].Rect));
		Assert.False(p.Repark(99));
	}

	[Fact]
	public void A_window_minimized_while_it_was_parked_comes_back_restored_where_it_was()
	{
		var p = Create();
		p.Park(1);
		_placer.Windows[1] = _placer.Windows[1] with { Minimized = true, Rect = new Area(-32000, -32000, -31840, -31972) };

		Assert.True(p.Unpark(1));

		Assert.False(_placer.Windows[1].Minimized);
		Assert.Equal((1L, Rect), _placer.Normalizes[^1]);
	}

	[Fact]
	public void A_maximized_window_minimized_while_it_was_parked_comes_back_maximized()
	{
		CreateMaximized(Rect);
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);
		p.Park(1);
		_placer.Windows[1] = _placer.Windows[1] with { Minimized = true, Rect = new Area(-32000, -32000, -31840, -31972) };
		_placer.MaximizedWindows.Remove(1);

		Assert.True(p.Unpark(1));

		Assert.True(_placer.IsMaximized(1));
		Assert.False(_placer.Windows[1].Minimized);
		Assert.Equal((1L, Rect), _placer.Maximizes[^1]);
	}

	[Fact]
	public void A_minimized_window_is_not_parked_again()
	{
		var p = Create();
		p.Park(1);
		_placer.Windows[1] = _placer.Windows[1] with { Minimized = true };
		_placer.MonitorList.Add(new Area(-1920, 0, 0, 1080));

		Assert.Equal(0, p.Repark());
	}

	[Fact]
	public void A_snapped_window_minimized_while_it_was_parked_does_not_stay_at_its_parking_spot()
	{
		// 25.11. 11:57:59: Claude, snapped, came back restored at its parking spot (-1894,0,-160,1399) and stayed out of sight
		var p = Create();
		p.Park(1);
		var spot = p.ParkedRectOf(1)!.Value;
		_placer.RestoresAt[1] = spot;
		_placer.Windows[1] = _placer.Windows[1] with { Minimized = true, Rect = new Area(-32000, -32000, -31840, -31972) };

		Assert.True(p.Unpark(1));

		Assert.False(_placer.Windows[1].Minimized);
		Assert.Equal(Rect, _placer.Windows[1].Rect);
	}

	[Fact]
	public void A_window_mostly_out_of_the_screen_did_not_come_back_on_its_own()
	{
		var p = Create();
		p.Park(1);

		// 100 of 800 pixels on the monitor
		_placer.Windows[1] = _placer.Windows[1] with { Rect = new Area(-700, 200, 100, 700) };
		Assert.True(p.Unpark(1));

		Assert.Equal(Rect, _placer.Windows[1].Rect);
	}

	[Fact]
	public void A_window_whose_place_is_mostly_on_an_unplugged_monitor_comes_back_completely_on_one_that_exists()
	{
		// 25.11. 12:02:46: KiTTY came back at (1746,270,2421,695) on a monitor 1920 wide
		_placer.MonitorList.Add(new Area(1920, 0, 3840, 1080));
		var original = new Area(1746, 270, 2421, 695);
		_placer.Windows[1] = (100, original, false);
		var p = new WindowParker(_placer, _journal);
		p.Park(1);

		_placer.MonitorList.RemoveAt(1);
		p.Unpark(1);

		var back = _placer.Windows[1].Rect;
		Assert.Equal(new Area(1920 + ScreenFit.InvisibleBorder - 675, 270, 1920 + ScreenFit.InvisibleBorder, 695), back);
	}

	[Fact]
	public void A_window_larger_than_the_monitor_that_remains_is_made_smaller()
	{
		_placer.MonitorList.Add(new Area(1920, 0, 5360, 1440));
		_placer.Windows[1] = (100, new Area(2000, 100, 4800, 1300), false);
		var p = new WindowParker(_placer, _journal);
		p.Park(1);

		_placer.MonitorList.RemoveAt(1);
		p.Unpark(1);

		var (window, rect, maximized) = _placer.Places[^1];
		Assert.Equal(1, window);
		Assert.False(maximized);
		Assert.True(rect.Width <= 1920 + 2 * ScreenFit.InvisibleBorder);
		Assert.True(rect.Height <= 1080 + ScreenFit.InvisibleBorder);
		Assert.Equal(rect, _placer.Windows[1].Rect);
	}

	[Fact]
	public void A_maximized_window_whose_monitor_was_unplugged_is_maximized_on_one_that_exists()
	{
		_placer.MonitorList.Add(new Area(1920, 0, 3840, 1080));
		_placer.Windows[1] = (100, new Area(1912, -8, 3848, 1048), false);
		_placer.MaximizedWindows.Add(1);
		_placer.RestoredRects[1] = new Area(2100, 100, 2900, 700);
		var p = new WindowParker(_placer, _journal, MaximizedParking.MoveMaximized);
		p.Park(1);

		_placer.MonitorList.RemoveAt(1);
		p.Unpark(1);

		var (window, rect, maximized) = _placer.Places[^1];
		Assert.Equal(1, window);
		Assert.True(maximized);
		Assert.True(ScreenFit.IsMostlyVisible(rect, _placer.MonitorList));
	}

	[Fact]
	public void A_window_the_monitor_change_made_larger_is_parked_again_with_its_new_size()
	{
		// 25.11. 12:07:06: Teams grew to 1936 pixels at its parking spot and stood on the monitor, parking it again did not move it
		var p = Create();
		p.Park(1);
		var spot = p.ParkedRectOf(1)!.Value;
		_placer.Windows[1] = _placer.Windows[1] with { Rect = new Area(spot.Left, spot.Top, spot.Left + 1936, spot.Top + 1168) };

		Assert.Equal(1, p.Repark());

		Assert.DoesNotContain(_placer.MonitorList, m => FlightPlanner.Intersects(m, _placer.Windows[1].Rect));
	}

	[Fact]
	public void A_shown_window_mostly_out_of_the_screen_is_put_back_and_keeps_its_size()
	{
		var p = Create();
		_placer.Windows[1] = (100, new Area(-1894, 0, -160, 1000), false);

		Assert.True(p.EnsureOnScreen(1));

		var back = _placer.Windows[1].Rect;
		Assert.True(ScreenFit.IsMostlyVisible(back, _placer.MonitorList));
		Assert.Equal(1734, back.Width);
		Assert.Empty(_placer.Places);
	}

	[Fact]
	public void A_shown_maximized_window_mostly_out_of_the_screen_is_maximized_on_a_monitor()
	{
		// 25.11. 12:02:48: Teams maximized at (-1696,259,240,1427)
		var p = Create();
		_placer.Windows[1] = (100, new Area(-1696, 259, 240, 1427), false);
		_placer.MaximizedWindows.Add(1);
		_placer.RestoredRects[1] = new Area(94, 70, 1630, 934);

		Assert.True(p.EnsureOnScreen(1));

		Assert.Equal((1L, new Area(94, 70, 1630, 934), true), _placer.Places[^1]);
	}

	[Fact]
	public void Windows_on_the_screen_parked_or_minimized_are_not_put_back()
	{
		var p = Create();
		_placer.Windows[2] = (100, new Area(-1894, 0, -160, 1000), true);
		_placer.Windows[3] = (100, new Area(-300, 0, 500, 600), false);

		Assert.False(p.EnsureOnScreen(1));
		Assert.False(p.EnsureOnScreen(2));
		Assert.False(p.EnsureOnScreen(3));

		p.Park(1);
		Assert.False(p.EnsureOnScreen(1));
	}
}
