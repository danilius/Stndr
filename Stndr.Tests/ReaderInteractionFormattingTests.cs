using Avalonia.Input;
using Xunit;

namespace Stndr.Tests;

public sealed class ReaderInteractionFormattingTests
{
    [Theory]
    [InlineData("2a", "From What Time", "מאימתי", "פרק מאימתי — דף ב ע״א / Chapter From What Time — Daf 2a")]
    [InlineData("2b", "Chapter From What Time", "פרק מאימתי", "פרק מאימתי — דף ב ע״ב / Chapter From What Time — Daf 2b")]
    [InlineData("30a", "", "", "דף ל ע״א / Daf 30a")]
    public void Talmud_header_uses_both_languages_with_chapter_daf_and_amud(
        string page,
        string englishChapter,
        string hebrewChapter,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindow.FormatTalmudPageHeader(page, englishChapter, hebrewChapter));
    }

    [Theory]
    [InlineData("פרק א", "Chapter 1", "פרק א / Chapter 1")]
    [InlineData("", "Introduction", "Introduction")]
    [InlineData("הקדמה", "", "הקדמה")]
    public void Page_header_joins_available_languages(
        string hebrew,
        string english,
        string expected)
    {
        Assert.Equal(expected, MainWindow.JoinBilingualPageHeader(hebrew, english));
    }
    [Theory]
    [InlineData(Key.Tab, KeyModifiers.None, false, true)]
    [InlineData(Key.Tab, KeyModifiers.None, true, false)]
    [InlineData(Key.Tab, KeyModifiers.Shift, false, false)]
    [InlineData(Key.Tab, KeyModifiers.Control, false, false)]
    [InlineData(Key.Enter, KeyModifiers.None, false, false)]
    public void Side_panel_shortcut_only_uses_plain_Tab_outside_text_entry(
        Key key,
        KeyModifiers modifiers,
        bool textEntryFocused,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldToggleSidePanelsForKey(key, modifiers, textEntryFocused));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Side_panel_shortcut_hides_if_either_panel_is_visible(
        bool leftCollapsed,
        bool rightCollapsed,
        bool expectedCollapse)
    {
        Assert.Equal(
            expectedCollapse,
            MainWindow.ShouldCollapseBothSidePanels(leftCollapsed, rightCollapsed));
    }
}
