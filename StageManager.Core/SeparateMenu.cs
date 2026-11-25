namespace StageManager.Core;

/// <summary>A window of a card, as the menu "Separate from group" needs it.</summary>
public sealed record CardWindow(long Handle, string AppKey, string AppName, string Title);

public enum SeparateAction
{
	/// <summary>All windows of the app leave the card for the card of the app (or a new one).</summary>
	App,

	/// <summary>The window gets a card of its own, other windows of its app stay.</summary>
	Window
}

/// <param name="Label">The text of the entry. For <see cref="SeparateAction.App"/> the name of the app, to be put into "All {0} windows".</param>
/// <param name="Handle">The window, 0 for <see cref="SeparateAction.App"/>.</param>
public sealed record SeparateEntry(SeparateAction Action, string Label, string AppKey, long Handle);

/// <summary>
/// The entries of the menu "Separate from group" of a card: one per window, and one for all windows of an app that has several windows
/// on a card of several apps. Windows with the same title (two KiTTY windows of the same server) are numbered from the front.
/// </summary>
public static class SeparateMenu
{
	/// <summary>Titles longer than this are cut in the menu.</summary>
	public const int MaxTitleLength = 60;

	/// <param name="backToFront">The windows of the card, back to front as the card stacks them.</param>
	/// <returns>Empty if there is nothing to separate: the card has only one window.</returns>
	public static IReadOnlyList<SeparateEntry> Build(IReadOnlyList<CardWindow> backToFront)
	{
		if (backToFront.Count < 2)
			return Array.Empty<SeparateEntry>();

		var comparer = StringComparer.OrdinalIgnoreCase;
		var apps = backToFront.GroupBy(w => w.AppKey, comparer).ToList();
		var severalApps = apps.Count > 1;
		var entries = new List<SeparateEntry>();

		if (severalApps)
		{
			foreach (var app in apps.Where(a => a.Count() > 1))
				entries.Add(new SeparateEntry(SeparateAction.App, app.First().AppName, app.Key, 0));
		}

		var frontToBack = backToFront.Reverse().ToList();
		var labels = frontToBack.Select(Label).ToList();
		var seen = new Dictionary<string, int>(StringComparer.Ordinal);

		for (var i = 0; i < frontToBack.Count; i++)
		{
			var window = frontToBack[i];
			var label = labels[i];

			if (labels.Count(l => l == label) > 1)
			{
				seen[label] = seen.GetValueOrDefault(label) + 1;
				label = $"{label} ({seen[label]})";
			}

			// the only window of its app on a card of several apps: the app leaves and new windows of it join it again
			var alone = severalApps && backToFront.Count(w => comparer.Equals(w.AppKey, window.AppKey)) == 1;
			entries.Add(new SeparateEntry(alone ? SeparateAction.App : SeparateAction.Window, label, window.AppKey, window.Handle));
		}

		return entries;
	}

	private static string Label(CardWindow window)
	{
		var title = string.IsNullOrWhiteSpace(window.Title) ? window.AppName : window.Title.Trim();
		return title.Length > MaxTitleLength ? title[..(MaxTitleLength - 3)] + "..." : title;
	}
}
