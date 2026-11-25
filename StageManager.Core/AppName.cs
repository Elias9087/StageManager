namespace StageManager.Core;

/// <summary>
/// The name an app is shown by. The file description is mostly the name ("Google Chrome"), but some apps describe what they do
/// there ("SSH, Telnet, Rlogin, and SUPDUP client" for KiTTY). Then the product name is used, and the process name if both are no name.
/// </summary>
public static class AppName
{
	/// <summary>Longer texts are a description, not a name.</summary>
	public const int MaxLength = 30;

	public static string Choose(string? fileDescription, string? productName, string fallback)
	{
		if (IsName(fileDescription))
			return fileDescription!.Trim();

		if (IsName(productName))
			return productName!.Trim();

		return fallback;
	}

	private static bool IsName(string? text) =>
		!string.IsNullOrWhiteSpace(text) && text.Trim().Length <= MaxLength && !text.Contains(',');
}
