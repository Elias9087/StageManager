namespace StageManager.Core;

public enum WindowDropKind
{
	/// <summary>Letting go does nothing, the window stays where it was dropped.</summary>
	None,

	/// <summary>The window goes into the scene of a card.</summary>
	Card,

	/// <summary>The window gets a card of its own.</summary>
	NewCard
}

/// <summary>Where a window dragged at its title bar into the sidebar would go.</summary>
public readonly record struct WindowDropTarget(WindowDropKind Kind, Guid? Card = null)
{
	public static WindowDropTarget None => new(WindowDropKind.None);

	public static WindowDropTarget NewCard => new(WindowDropKind.NewCard);

	public static WindowDropTarget OnCard(Guid card) => new(WindowDropKind.Card, card);

	public bool IsNone => Kind == WindowDropKind.None;
}

/// <summary>
/// Finds the target of a window that is dragged at its title bar into the sidebar: a card, or a free place for a card of its own.
/// All values in physical pixels.
/// </summary>
public sealed class WindowDropTargets
{
	/// <summary>
	/// Pixels at the screen edge and at the top that never count: there Windows takes over and snaps the window (Aero Snap).
	/// </summary>
	public const double EdgeGuard = 8;

	private readonly CardDropTargets _cards = new();

	public WindowDropTarget Target { get; private set; } = WindowDropTarget.None;

	/// <summary>Escape was pressed during the drag: nothing is a target any more until the next drag.</summary>
	public bool IsCancelled { get; private set; }

	/// <param name="cards">The cards in the sidebar, the whole card with its icons.</param>
	/// <param name="sidebarShown">The sidebar is on the screen. A hidden one has no targets, it comes in first.</param>
	/// <param name="allowNewCard">False if the window is alone on the stage: a card of its own would change nothing.</param>
	/// <returns>True if the target changed.</returns>
	public bool Update(double x, double y, SidebarGeometry geometry, IReadOnlyList<(Guid Id, Area Area)> cards, bool sidebarShown, bool allowNewCard,
		double edgeGuard = EdgeGuard)
	{
		var next = Find(x, y, geometry, cards, sidebarShown, allowNewCard, edgeGuard);
		var changed = next != Target;
		Target = next;
		return changed;
	}

	private WindowDropTarget Find(double x, double y, SidebarGeometry geometry, IReadOnlyList<(Guid Id, Area Area)> cards, bool sidebarShown, bool allowNewCard,
		double edgeGuard)
	{
		if (IsCancelled || !sidebarShown || !IsInDropZone(x, y, geometry, edgeGuard))
		{
			_cards.Clear();
			return WindowDropTarget.None;
		}

		if (_cards.Update(x, y, cards) is Guid card)
			return WindowDropTarget.OnCard(card);

		return allowNewCard ? WindowDropTarget.NewCard : WindowDropTarget.None;
	}

	/// <summary>True if the pointer is above the sidebar, but not at the screen edge or the top where Windows would snap the window.</summary>
	public static bool IsInDropZone(double x, double y, SidebarGeometry geometry, double edgeGuard = EdgeGuard)
	{
		var area = geometry.Area;
		if (y < area.Top + edgeGuard || y > area.Bottom)
			return false;

		if (!geometry.IsOverSidebar(x))
			return false;

		return geometry.Side == SidebarSide.Left ? x >= area.Left + edgeGuard : x <= area.Right - edgeGuard;
	}

	/// <summary>Escape: no target until the drag is over.</summary>
	/// <returns>True if the target changed.</returns>
	public bool Cancel()
	{
		IsCancelled = true;
		_cards.Clear();
		var changed = !Target.IsNone;
		Target = WindowDropTarget.None;
		return changed;
	}

	/// <summary>Ready for the next drag.</summary>
	public void Reset()
	{
		IsCancelled = false;
		_cards.Clear();
		Target = WindowDropTarget.None;
	}
}

/// <summary>Decides whether Windows started to move a window at its title bar, not to resize it.</summary>
public static class WindowDragStart
{
	public const int HTCLIENT = 1;
	public const int HTCAPTION = 2;

	/// <summary>HTLEFT to HTBOTTOMRIGHT and HTBORDER: the edges that resize a window.</summary>
	public const int HTSIZEFIRST = 10;
	public const int HTSIZELAST = 18;

	/// <summary>Height of the band at the top of a window that counts as title bar if the window does not say what is under the pointer, in DIP.</summary>
	public const double CaptionBand = 48;

	/// <param name="hitTest">What the window answered to WM_NCHITTEST at the pointer, null if it did not answer in time.</param>
	/// <param name="window">The window rectangle when the drag started.</param>
	/// <param name="captionBand">Height of the title bar band in physical pixels.</param>
	public static bool IsTitleBarDrag(bool buttonDown, int? hitTest, Area window, double x, double y, double captionBand)
	{
		// moving with the keyboard (Alt+Space, Move) has no button pressed
		if (!buttonDown)
			return false;

		if (hitTest == HTCAPTION)
			return true;

		if (hitTest is >= HTSIZEFIRST and <= HTSIZELAST)
			return false;

		// no answer, or the client area: apps with their own title bar (browsers, Electron) draw it into the client area and start
		// the move themselves. Then only the band at the top counts
		return x >= window.Left && x <= window.Right && y >= window.Top && y <= window.Top + captionBand;
	}
}

public enum WindowDropOutcome
{
	/// <summary>The window goes into the target.</summary>
	Drop,

	/// <summary>Let go outside the sidebar, or where nothing is a target.</summary>
	NoTarget,

	/// <summary>Escape or a right click: Windows put the window back where the drag started.</summary>
	Cancelled
}

/// <summary>Decides what letting go of a window dragged at its title bar does.</summary>
public static class WindowDragEnd
{
	/// <summary>Pixels a window may be off its start rectangle and still count as put back by Windows.</summary>
	public const double SameTolerance = 2;

	/// <param name="escapeSeen">Escape was pressed during the drag.</param>
	/// <param name="start">The window rectangle when the drag started.</param>
	/// <param name="end">The window rectangle when the drag ended.</param>
	public static WindowDropOutcome Decide(WindowDropTarget target, bool escapeSeen, Area start, Area end)
	{
		if (escapeSeen)
			return WindowDropOutcome.Cancelled;

		if (target.IsNone)
			return WindowDropOutcome.NoTarget;

		// Windows puts the window back when the move is cancelled, the pointer may still be above a card then
		return IsSame(start, end) ? WindowDropOutcome.Cancelled : WindowDropOutcome.Drop;
	}

	public static bool IsSame(Area a, Area b) =>
		Math.Abs(a.Left - b.Left) <= SameTolerance && Math.Abs(a.Top - b.Top) <= SameTolerance
		&& Math.Abs(a.Right - b.Right) <= SameTolerance && Math.Abs(a.Bottom - b.Bottom) <= SameTolerance;
}

/// <summary>What happens to the scenes when a window of the stage is dropped onto the sidebar.</summary>
public enum WindowDropPlan
{
	/// <summary>Nothing happens.</summary>
	Ignore,

	/// <summary>The window leaves the stage and gets a card of its own, the scene stays on the stage.</summary>
	OwnCard,

	/// <summary>The window leaves the stage for the scene of the card, the scene stays on the stage.</summary>
	IntoCard,

	/// <summary>The window was alone on the stage: it goes into the scene of the card, and that scene takes the stage.</summary>
	IntoCardAndSwitch
}

public static class WindowDropRules
{
	/// <param name="windowsOnStage">Number of windows of the scene on the stage, the dragged one included.</param>
	/// <param name="targetIsStage">The card belongs to the scene on the stage (cannot happen in the sidebar, but is ignored).</param>
	public static WindowDropPlan Plan(WindowDropTarget target, int windowsOnStage, bool targetIsStage = false)
	{
		if (windowsOnStage < 1)
			return WindowDropPlan.Ignore;

		var alone = windowsOnStage == 1;

		return target.Kind switch
		{
			WindowDropKind.NewCard => alone ? WindowDropPlan.Ignore : WindowDropPlan.OwnCard,
			WindowDropKind.Card when targetIsStage || target.Card is null => WindowDropPlan.Ignore,
			WindowDropKind.Card => alone ? WindowDropPlan.IntoCardAndSwitch : WindowDropPlan.IntoCard,
			_ => WindowDropPlan.Ignore
		};
	}
}
