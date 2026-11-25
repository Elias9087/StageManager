namespace StageManager.Core;

/// <summary>What is known about a top level window when it is decided if StageManager should manage it.</summary>
public readonly record struct WindowFacts(
	string ClassName,
	string ProcessName,
	bool IsRoot,
	bool IsMinimized,
	int Width,
	int Height,
	bool IsResizable = true);

/// <summary>Rules which windows are left alone. Has no Win32 dependencies.</summary>
public static class WindowFilter
{
	public const int MinWidth = 40;
	public const int MinHeight = 30;

	private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
	{
		"TaskManagerWindow",
		"MSCTFIME UI",
		"SHELLDLL_DefView",
		"LockScreenBackstopFrame",
		"Progman",
		"WorkerW",
		"Shell_TrayWnd", // Windows 11 start
		"Shell_SecondaryTrayWnd",
		// the frame window of the app is managed, not its inner window
		"Windows.UI.Core.CoreWindow",
		"XamlExplorerHostIslandWindow",
		"DesktopWindowContentBridge"
	};

	private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
	{
		"SearchUI",
		"ShellExperienceHost",
		"PeopleExperienceHost",
		"LockApp",
		"StartMenuExperienceHost",
		"SearchApp",
		"SearchHost", // Windows 11 search
		"search", // Windows 11 RTM search
		"ScreenClippingHost"
	};

	/// <summary>The window class of standard dialogs of Windows.</summary>
	public const string DialogClass = "#32770";

	public static bool IsCandidate(WindowFacts window)
	{
		if (!window.IsRoot)
			return false;

		// a dialog of a fixed size is no window to work in, e.g. the session launcher of KiTTY or PuTTY, Win+R or the properties of a file:
		// it would get a card of its own and take the stage only for a moment
		if (IsFixedDialog(window))
			return false;

		if (IgnoredClasses.Contains(window.ClassName) || IgnoredProcesses.Contains(window.ProcessName))
			return false;

		// minimized windows report the size of their caption bar, tiny helper windows are of no use
		if (!window.IsMinimized && (window.Width < MinWidth || window.Height < MinHeight))
			return false;

		return true;
	}

	public static bool IsFixedDialog(WindowFacts window) =>
		string.Equals(window.ClassName, DialogClass, StringComparison.Ordinal) && !window.IsResizable;
}
