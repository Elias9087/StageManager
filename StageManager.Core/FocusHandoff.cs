namespace StageManager.Core;

/// <summary>
/// When the active window closes, hides or minimizes, Windows hands the focus to another window. That can be a window that is not on
/// the screen, e.g. a parked one: Windows treats it like any other visible window. Such a handoff must not switch the scene, the
/// user did not ask for it. Knows nothing about Win32, windows are identified by their handle as a number.
/// </summary>
public sealed class FocusHandoff
{
	/// <summary>How long after the active window vanished an activation counts as a handoff by Windows.</summary>
	public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(300);

	private readonly Func<DateTime> _clock;
	private readonly TimeSpan _window;
	private long? _active;
	private DateTime? _vanishedAt;

	public FocusHandoff(Func<DateTime>? clock = null, TimeSpan? window = null)
	{
		_clock = clock ?? (() => DateTime.UtcNow);
		_window = window ?? DefaultWindow;
	}

	/// <summary>The window that has the focus now.</summary>
	public long? Active => _active;

	/// <summary>A window got the focus.</summary>
	public void Activated(long window) => _active = window;

	/// <summary>A window closed, hid or minimized itself. Only the active window makes Windows hand the focus on.</summary>
	public void Vanished(long window)
	{
		if (_active != window)
			return;

		_active = null;
		_vanishedAt = _clock();
	}

	/// <summary>
	/// True if a window that got the focus was most likely given it by Windows because the active window vanished, not by the user.
	/// </summary>
	/// <param name="targetOnScreen">The window that got the focus is visible on a monitor, not parked, minimized or hidden.</param>
	/// <param name="targetInCurrentScene">The window that got the focus belongs to the scene on the stage.</param>
	/// <param name="currentSceneHasWindowOnScreen">The scene on the stage still has a window on the screen that can take the focus.</param>
	public bool IsHandoff(bool targetOnScreen, bool targetInCurrentScene, bool currentSceneHasWindowOnScreen)
	{
		if (targetOnScreen || targetInCurrentScene || !currentSceneHasWindowOnScreen)
			return false;

		return _vanishedAt is DateTime vanished && _clock() - vanished <= _window;
	}

	/// <summary>
	/// True if the focus rests on a window that is not on the screen and not part of the scene on the stage, while that scene has a
	/// window that could take it. Typing would go to a window nobody sees.
	/// </summary>
	public static bool IsStranded(bool activeOnScreen, bool activeInCurrentScene, bool currentSceneHasWindowOnScreen) =>
		!activeOnScreen && !activeInCurrentScene && currentSceneHasWindowOnScreen;
}
