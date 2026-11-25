using StageManager.Core;

namespace StageManager.Tests;

public class SeparateMenuTests
{
	private static CardWindow Kitty(long handle, string title = "rockydbserver - KiTTY") => new(handle, "KITTY", "KiTTY", title);

	private static CardWindow Notepad(long handle) => new(handle, "NOTEPAD", "Editor", "notes.txt - Editor");

	[Fact]
	public void A_card_with_one_window_has_nothing_to_separate()
	{
		Assert.Empty(SeparateMenu.Build(new[] { Kitty(1) }));
	}

	[Fact]
	public void Two_windows_of_one_app_get_one_entry_each_and_no_entry_for_the_app()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1, "a - KiTTY"), Kitty(2, "b - KiTTY") });

		Assert.Equal(2, entries.Count);
		Assert.All(entries, e => Assert.Equal(SeparateAction.Window, e.Action));
	}

	[Fact]
	public void The_front_window_comes_first()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1, "back - KiTTY"), Kitty(2, "front - KiTTY") });

		Assert.Equal(new long[] { 2, 1 }, entries.Select(e => e.Handle));
	}

	[Fact]
	public void Windows_with_the_same_title_are_numbered_from_the_front()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1), Kitty(2), Kitty(3, "other - KiTTY") });

		Assert.Equal("other - KiTTY", entries[0].Label);
		Assert.Equal("rockydbserver - KiTTY (1)", entries[1].Label);
		Assert.Equal(2, entries[1].Handle);
		Assert.Equal("rockydbserver - KiTTY (2)", entries[2].Label);
	}

	[Fact]
	public void An_app_with_several_windows_on_a_card_of_several_apps_gets_an_entry_for_all_of_them()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1), Notepad(2), Kitty(3) });

		var app = Assert.Single(entries, e => e.Handle == 0);
		Assert.Equal(SeparateAction.App, app.Action);
		Assert.Equal("KiTTY", app.Label);
		Assert.Equal("KITTY", app.AppKey);
		Assert.Same(app, entries[0]);
	}

	[Fact]
	public void The_only_window_of_its_app_on_a_card_of_several_apps_separates_its_app()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1), Notepad(2), Kitty(3) });

		var notepad = entries.Single(e => e.Handle == 2);
		Assert.Equal(SeparateAction.App, notepad.Action);
		Assert.Equal("NOTEPAD", notepad.AppKey);
		Assert.All(entries.Where(e => e.AppKey == "KITTY" && e.Handle != 0), e => Assert.Equal(SeparateAction.Window, e.Action));
	}

	[Fact]
	public void A_window_without_a_title_shows_the_name_of_its_app()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1, ""), Notepad(2) });

		Assert.Equal("KiTTY", entries.Single(e => e.Handle == 1).Label);
	}

	[Fact]
	public void Long_titles_are_cut()
	{
		var entries = SeparateMenu.Build(new[] { Kitty(1, new string('x', 100)), Notepad(2) });

		var label = entries.Single(e => e.Handle == 1).Label;
		Assert.Equal(SeparateMenu.MaxTitleLength, label.Length);
		Assert.EndsWith("...", label);
	}
}
