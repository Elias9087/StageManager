using StageManager.Core;

namespace StageManager.Tests;

public class AppNameTests
{
	[Fact]
	public void The_file_description_is_the_name()
	{
		Assert.Equal("Google Chrome", AppName.Choose("Google Chrome", "Google Chrome", "chrome"));
	}

	[Fact]
	public void A_description_of_what_the_app_does_gives_way_to_the_product_name()
	{
		// kitty.exe
		Assert.Equal("KiTTY", AppName.Choose("SSH, Telnet, Rlogin, and SUPDUP client", "KiTTY", "kitty"));
	}

	[Fact]
	public void Without_a_usable_name_the_process_name_is_used()
	{
		Assert.Equal("kitty", AppName.Choose("SSH, Telnet, Rlogin, and SUPDUP client", "Microsoft® Windows® Operating System", "kitty"));
		Assert.Equal("app", AppName.Choose(null, " ", "app"));
	}

	[Fact]
	public void The_name_is_trimmed()
	{
		Assert.Equal("Notepad", AppName.Choose(" Notepad ", null, "notepad"));
	}
}
