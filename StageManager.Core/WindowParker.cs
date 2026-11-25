using System.Text.Json;

namespace StageManager.Core;

/// <summary>What the parker needs from Windows. Windows are identified by their handle as a number.</summary>
public interface IWindowPlacer
{
	/// <summary>True if the handle still belongs to the window of that process. Handles are reused after a window is gone.</summary>
	bool Exists(long window, int processId);

	int ProcessIdOf(long window);

	Area? GetRect(long window);

	/// <summary>The visible frame, which is what a thumbnail shows. Without the invisible borders of the window.</summary>
	Area? GetFrame(long window);

	bool IsMinimized(long window);

	bool IsMaximized(long window);

	/// <summary>The rectangle a maximized window gets back when it is restored, as Windows keeps it. Only handed back to <see cref="Maximize"/>.</summary>
	Area? GetRestoredRect(long window);

	/// <summary>
	/// Takes a maximized window out of the maximized state without changing what is seen: it keeps <paramref name="rect"/>.
	/// A maximized window cannot be moved, Windows puts it back.
	/// </summary>
	bool Normalize(long window, Area rect);

	/// <summary>Maximizes the window again, <paramref name="restored"/> is what it gets when it is restored later.</summary>
	bool Maximize(long window, Area restored);

	/// <summary>Moves the window so that its top left corner is at the one of <paramref name="rect"/>. The size does not change.</summary>
	bool Move(long window, Area rect, bool refreshFrame);

	/// <summary>
	/// Puts the window at <paramref name="rect"/>, in screen coordinates and with that size. With <paramref name="maximized"/> it is
	/// maximized on the monitor of <paramref name="rect"/>, which it gets back when it is restored. To rescue a window that is out of reach.
	/// </summary>
	bool Place(long window, Area rect, bool maximized);

	/// <summary>Visible windows that belong to the window, like dialogs.</summary>
	IReadOnlyList<long> OwnedWindows(long window);

	IReadOnlyList<Area> Monitors();

	/// <summary>The work area of each monitor, in the order of <see cref="Monitors"/>. Where a window goes whose monitor is gone.</summary>
	IReadOnlyList<Area> WorkAreas() => Monitors();
}

/// <summary>A window that was moved out of the screen and has to be brought back.</summary>
public sealed record ParkedWindow
{
	public long Window { get; init; }

	public int ProcessId { get; init; }

	/// <summary>The window this one belongs to, or 0. Owned windows follow their owner.</summary>
	public long Owner { get; init; }

	public Area Original { get; init; }

	public Area OriginalFrame { get; init; }

	public Area Parked { get; init; }

	/// <summary>The window was maximized. It is parked in the normal state and maximized again when it comes back.</summary>
	public bool Maximized { get; init; }

	/// <summary>The rectangle a maximized window gets when it is restored, null for other windows.</summary>
	public Area? Restored { get; init; }

	/// <summary>The window was maximized and moved as it is (<see cref="MaximizedParking.MoveMaximized"/>). It is maximized again if it gets minimized meanwhile.</summary>
	public bool MovedMaximized { get; init; }
}

/// <summary>Remembers parked windows, so they come back even if the app was killed.</summary>
public interface IParkingJournal
{
	IReadOnlyList<ParkedWindow> Load();

	void Save(IReadOnlyList<ParkedWindow> entries);
}

public sealed class FileParkingJournal : IParkingJournal
{
	private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

	private readonly string _filePath;

	public FileParkingJournal(string filePath)
	{
		_filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
	}

	public IReadOnlyList<ParkedWindow> Load()
	{
		try
		{
			if (!File.Exists(_filePath))
				return Array.Empty<ParkedWindow>();

			return JsonSerializer.Deserialize<List<ParkedWindow>>(File.ReadAllText(_filePath), Options) ?? new List<ParkedWindow>();
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			return Array.Empty<ParkedWindow>();
		}
	}

	/// <summary>Written through a temporary file, so a crash while saving cannot leave half a journal behind.</summary>
	public void Save(IReadOnlyList<ParkedWindow> entries)
	{
		try
		{
			if (entries.Count == 0)
			{
				File.Delete(_filePath);
				return;
			}

			var directory = Path.GetDirectoryName(_filePath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			var temp = _filePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(entries, Options));
			File.Move(temp, _filePath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// without a journal windows are only lost if the app is killed while they are parked
		}
	}
}

/// <summary>How maximized windows are parked.</summary>
public enum MaximizedParking
{
	/// <summary>The window leaves the maximized state, is moved and maximized again when it comes back.</summary>
	Normalize,

	/// <summary>
	/// The window is moved as it is, still maximized. Nothing is resized, so Chromium based windows (Electron apps like Claude) do not
	/// show a black area when they come back, which they do after being maximized again. Tested on Windows 11: the window stays out of
	/// the screen and maximized.
	/// </summary>
	MoveMaximized
}

/// <summary>
/// Hides windows by moving them out of the screen and brings them back. Unlike minimizing, this keeps the picture
/// DWM shows of a window: Chromium based windows lose theirs when they are minimized.
/// </summary>
public sealed class WindowParker
{
	private readonly IWindowPlacer _placer;
	private readonly IParkingJournal _journal;
	private readonly MaximizedParking _maximizedParking;
	private readonly object _lock = new();
	private readonly List<ParkedWindow> _entries = new();

	public WindowParker(IWindowPlacer placer, IParkingJournal journal, MaximizedParking maximizedParking = MaximizedParking.Normalize)
	{
		_placer = placer ?? throw new ArgumentNullException(nameof(placer));
		_journal = journal ?? throw new ArgumentNullException(nameof(journal));
		_maximizedParking = maximizedParking;
	}

	public int Count
	{
		get
		{
			lock (_lock)
				return _entries.Count;
		}
	}

	public bool IsParked(long window)
	{
		lock (_lock)
			return _entries.Any(e => e.Window == window);
	}

	/// <summary>Where a parked window was before, null if it is not parked.</summary>
	public Area? OriginalRectOf(long window)
	{
		lock (_lock)
			return _entries.FirstOrDefault(e => e.Window == window)?.Original;
	}

	/// <summary>Where a parked window was moved to, null if it is not parked.</summary>
	public Area? ParkedRectOf(long window)
	{
		lock (_lock)
			return _entries.FirstOrDefault(e => e.Window == window)?.Parked;
	}

	/// <summary>The visible frame a parked window had before, null if it is not parked.</summary>
	public Area? OriginalFrameOf(long window)
	{
		lock (_lock)
			return _entries.FirstOrDefault(e => e.Window == window)?.OriginalFrame;
	}

	/// <summary>Moves a window and the windows that belong to it out of the screen.</summary>
	/// <returns>False if it cannot be parked: it is minimized already, gone or there is no free place. Then it has to be hidden another way.</returns>
	public bool Park(long window)
	{
		lock (_lock)
		{
			if (_entries.Any(e => e.Window == window))
				return true;

			var processId = _placer.ProcessIdOf(window);
			if (!_placer.Exists(window, processId) || _placer.IsMinimized(window))
				return false;

			if (_placer.GetRect(window) is not Area rect)
				return false;

			if (ParkingSpot.Find(rect, _placer.Monitors()) is not Area spot)
				return false;

			var dx = spot.Left - rect.Left;
			var dy = spot.Top - rect.Top;

			// with Normalize a maximized window leaves that state before it is moved and is maximized again when it comes back.
			// Moved as it is, it keeps the maximized state on its own and only has to be moved back
			var isMaximized = _placer.IsMaximized(window);
			var maximized = isMaximized && _maximizedParking == MaximizedParking.Normalize;
			var movedMaximized = isMaximized && !maximized;

			// the restored rectangle is needed to maximize it again, also when a window moved as it is was minimized while it was parked
			var restored = isMaximized ? _placer.GetRestoredRect(window) : null;
			if (maximized && restored is null)
				return false;

			var parked = new List<ParkedWindow> { Entry(window, processId, 0, rect, spot) with { Maximized = maximized, Restored = restored, MovedMaximized = movedMaximized } };

			foreach (var owned in _placer.OwnedWindows(window))
			{
				if (_entries.Any(e => e.Window == owned) || _placer.IsMinimized(owned) || _placer.GetRect(owned) is not Area ownedRect)
					continue;

				var target = new Area(ownedRect.Left + dx, ownedRect.Top + dy, ownedRect.Right + dx, ownedRect.Bottom + dy);
				parked.Add(Entry(owned, _placer.ProcessIdOf(owned), window, ownedRect, target));
			}

			// the journal first: if the app dies right after the move, the window has to be found again
			_entries.AddRange(parked);
			_journal.Save(_entries);

			if (maximized && !_placer.Normalize(window, rect))
			{
				_entries.RemoveAll(parked.Contains);
				_journal.Save(_entries);
				return false;
			}

			foreach (var entry in parked)
				_placer.Move(entry.Window, entry.Parked, refreshFrame: false);

			return true;
		}
	}

	/// <summary>Brings a parked window and the windows that belong to it back to where they were.</summary>
	public bool Unpark(long window)
	{
		lock (_lock)
		{
			var entries = _entries.Where(e => e.Window == window || e.Owner == window).ToList();
			if (entries.Count == 0)
				return false;

			foreach (var entry in entries)
				Bring(entry);

			_entries.RemoveAll(entries.Contains);
			_journal.Save(_entries);
			return true;
		}
	}

	/// <summary>Forgets a window that is gone, without moving anything.</summary>
	public void Forget(long window)
	{
		lock (_lock)
		{
			if (_entries.RemoveAll(e => e.Window == window) > 0)
				_journal.Save(_entries);
		}
	}

	/// <summary>Brings back every window. For the end of the app and for emergencies.</summary>
	public int RestoreAll()
	{
		lock (_lock)
		{
			var restored = _entries.Count;

			foreach (var entry in _entries.ToList())
				Bring(entry);

			_entries.Clear();
			_journal.Save(_entries);
			return restored;
		}
	}

	/// <summary>
	/// Brings back the windows a previous run left out of the screen, e.g. because it was killed. Entries of windows that are gone are dropped.
	/// </summary>
	public int Recover()
	{
		lock (_lock)
		{
			var remembered = _journal.Load();
			var restored = 0;

			foreach (var entry in remembered)
			{
				if (!_placer.Exists(entry.Window, entry.ProcessId))
					continue;

				Bring(entry);
				restored++;
			}

			_entries.Clear();
			_journal.Save(_entries);
			return restored;
		}
	}

	private void Bring(ParkedWindow entry)
	{
		if (!_placer.Exists(entry.Window, entry.ProcessId))
			return;

		var monitors = _placer.Monitors();
		var workAreas = WorkAreasOf(monitors);

		// a window that came back on its own, or that the user placed on the screen, stays there, but completely on its monitor.
		// One still at its parking spot is not back: a monitor was plugged in there. One that is mostly out of the screen is not back either
		if (_placer.GetRect(entry.Window) is Area now && monitors.Count > 0 && ScreenFit.IsMostlyVisible(now, monitors) && !SnapGeometry.IsSame(now, entry.Parked))
		{
			FitOnScreen(entry.Window, now, monitors, workAreas);
			return;
		}

		var wasMaximized = entry.Maximized || entry.MovedMaximized;
		var minimized = _placer.IsMinimized(entry.Window);

		// Windows may have maximized it while it was parked, e.g. when the monitors changed
		var maximizedNow = !minimized && _placer.IsMaximized(entry.Window);

		if (wasMaximized || maximizedNow)
		{
			// moved as it is and still maximized as on a monitor that exists: only moved back, nothing is resized
			if (entry.MovedMaximized && maximizedNow && ScreenFit.IsCleanlyMaximized(entry.Original, monitors, workAreas))
			{
				_placer.Move(entry.Window, entry.Original, refreshFrame: true);
				return;
			}

			// maximized on a monitor that exists, with a restored rectangle that lies completely on it. A window that is maximized
			// already has to leave that state first, Windows does not move it otherwise
			var restored = ScreenFit.Fit(entry.Restored ?? entry.Original, monitors, workAreas);
			if (maximizedNow ? _placer.Place(entry.Window, restored, maximized: true) : _placer.Maximize(entry.Window, restored))
				return;
		}

		// completely on a monitor that exists, made smaller if it is too large for it
		var target = Rescued(entry, monitors, workAreas);

		// minimized while it was parked (e.g. a click on its button in the taskbar): it comes back restored
		if (minimized && _placer.Normalize(entry.Window, target))
		{
			// a snapped window comes back where it was snapped last, that is its parking spot: it is moved from there
			if (_placer.GetRect(entry.Window) is Area restoredAt && !SnapGeometry.IsSame(restoredAt, target, 1))
				_placer.Move(entry.Window, target, refreshFrame: true);

			return;
		}

		// made smaller to fit onto a monitor
		if (ScreenFit.IsResized(entry.Original, target) && _placer.Place(entry.Window, target, maximized: false))
			return;

		_placer.Move(entry.Window, target, refreshFrame: true);
	}

	/// <summary>
	/// Puts a shown window completely onto a monitor if it sticks out, e.g. because Windows or its app moved it when the monitors
	/// changed. A maximized window is maximized cleanly on a monitor, the others are put into a work area. Parked windows stay.
	/// </summary>
	/// <returns>True if the window was put back.</returns>
	public bool EnsureOnScreen(long window)
	{
		lock (_lock)
		{
			if (_entries.Any(e => e.Window == window) || _placer.IsMinimized(window) || _placer.GetRect(window) is not Area now)
				return false;

			var monitors = _placer.Monitors();
			return monitors.Count > 0 && FitOnScreen(window, now, monitors, WorkAreasOf(monitors));
		}
	}

	private bool FitOnScreen(long window, Area now, IReadOnlyList<Area> monitors, IReadOnlyList<Area> workAreas)
	{
		if (_placer.IsMaximized(window))
		{
			return !ScreenFit.IsCleanlyMaximized(now, monitors, workAreas)
				&& _placer.Place(window, ScreenFit.Fit(_placer.GetRestoredRect(window) ?? now, monitors, workAreas), maximized: true);
		}

		var target = ScreenFit.Fit(now, monitors, workAreas);
		if (SnapGeometry.IsSame(target, now, 1))
			return false;

		// a window of the same size is only moved, a snapped one stays snapped then
		return ScreenFit.IsResized(now, target)
			? _placer.Place(window, target, maximized: false)
			: _placer.Move(window, target, refreshFrame: true);
	}

	/// <summary>True if less than half of the window would be on a monitor.</summary>
	private static bool IsLost(Area rect, IReadOnlyList<Area> monitors) => monitors.Count > 0 && !ScreenFit.IsMostlyVisible(rect, monitors);

	/// <summary>
	/// Where a window goes back to: completely into the work area of a monitor. A maximized one keeps its place as long as it is on a
	/// monitor, it is maximized there again.
	/// </summary>
	private static Area Rescued(ParkedWindow entry, IReadOnlyList<Area> monitors, IReadOnlyList<Area> workAreas) =>
		(entry.Maximized || entry.MovedMaximized) && !IsLost(entry.Original, monitors)
			? entry.Original
			: ScreenFit.Fit(entry.Original, monitors, workAreas);

	private IReadOnlyList<Area> WorkAreasOf(IReadOnlyList<Area> monitors)
	{
		var workAreas = _placer.WorkAreas();
		return workAreas.Count == monitors.Count ? workAreas : monitors;
	}

	/// <summary>
	/// Parks the windows again that are not out of the screen any more after the monitors changed: a monitor was plugged in where they
	/// were parked, or Windows moved them onto a monitor that remained. A window whose monitor is gone comes back to one that exists.
	/// </summary>
	/// <returns>How many windows were parked again.</returns>
	public int Repark()
	{
		lock (_lock)
		{
			var count = 0;
			foreach (var entry in _entries.Where(e => e.Owner == 0).ToList())
			{
				if (Relocate(entry, force: false))
					count++;
			}

			if (count > 0)
				_journal.Save(_entries);

			return count;
		}
	}

	/// <summary>Parks a window once more, e.g. because it was found on a monitor right after it was parked.</summary>
	public bool Repark(long window)
	{
		lock (_lock)
		{
			if (_entries.FirstOrDefault(e => e.Window == window && e.Owner == 0) is not ParkedWindow entry || !Relocate(entry, force: true))
				return false;

			_journal.Save(_entries);
			return true;
		}
	}

	private bool Relocate(ParkedWindow entry, bool force)
	{
		if (!_placer.Exists(entry.Window, entry.ProcessId) || _placer.IsMinimized(entry.Window) || _placer.GetRect(entry.Window) is not Area now)
			return false;

		var monitors = _placer.Monitors();
		var visible = monitors.Any(m => FlightPlanner.Intersects(m, now));
		var lost = IsLost(entry.Original, monitors);

		if (!force && !visible && !lost)
			return false;

		var original = Rescued(entry, monitors, WorkAreasOf(monitors));

		// the spot fits the size the window has now: Windows or the app may have changed it with the monitors
		if (ParkingSpot.Find(new Area(original.Left, original.Top, original.Left + now.Width, original.Top + now.Height), monitors) is not Area spot)
			return false;

		// the window moves from where it is now, the ones that belong to it the same way
		var dx = spot.Left - now.Left;
		var dy = spot.Top - now.Top;
		var ox = original.Left - entry.Original.Left;
		var oy = original.Top - entry.Original.Top;

		foreach (var e in _entries.Where(e => e.Window == entry.Window || e.Owner == entry.Window).ToList())
		{
			var updated = e.Window == entry.Window
				? e with { Original = original, OriginalFrame = Shift(e.OriginalFrame, ox, oy), Parked = spot }
				: e with { Original = Shift(e.Original, ox, oy), OriginalFrame = Shift(e.OriginalFrame, ox, oy), Parked = Shift(_placer.GetRect(e.Window) ?? e.Parked, dx, dy) };

			_entries[_entries.IndexOf(e)] = updated;
			_placer.Move(updated.Window, updated.Parked, refreshFrame: false);
		}

		return true;
	}

	private static Area Shift(Area a, double dx, double dy) => new(a.Left + dx, a.Top + dy, a.Right + dx, a.Bottom + dy);

	private ParkedWindow Entry(long window, int processId, long owner, Area original, Area parked) => new()
	{
		Window = window,
		ProcessId = processId,
		Owner = owner,
		Original = original,
		OriginalFrame = _placer.GetFrame(window) ?? original,
		Parked = parked
	};
}
