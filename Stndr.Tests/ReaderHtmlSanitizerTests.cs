using Xunit;

namespace Stndr.Tests;

public sealed class ReaderHtmlSanitizerTests
{
    [Theory]
    [InlineData("<em>broken", "<em>broken</em>")]
    [InlineData("<strong><em>nested", "<strong><em>nested</em></strong>")]
    [InlineData("</em>plain", "plain")]
    [InlineData("<i>6</6>text", "text")]
    [InlineData("<em>closed</em> plain", "<em>closed</em> plain")]
    public void Sanitizer_contains_formatting_within_one_text_segment(
        string source,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindow.SanitizeReaderHtmlForWeb(
                source,
                isHebrew: false,
                HebrewMarksMode.NikkudAndCantillation));
    }

    [Fact]
    public void Sanitizer_does_not_emit_suppressed_footnote_numbers()
    {
        Assert.Equal("text", MainWindow.SanitizeReaderHtmlForWeb(
            "<sup>6</sup>text", false, HebrewMarksMode.NikkudAndCantillation));
    }

    [Theory]
    [InlineData("\u05D0\u05C0<span>\u05D1</span>", "\u05D0 \u05D1")]
    [InlineData("\u05D0\u05BE<span>\u05D1</span>", "\u05D0 \u05D1")]
    public void Text_only_preserves_word_spacing_when_a_separator_precedes_markup(
        string source,
        string expected)
    {
        Assert.Equal(expected, MainWindow.SanitizeReaderHtmlForWeb(
            source, true, HebrewMarksMode.TextOnly));
    }
}
