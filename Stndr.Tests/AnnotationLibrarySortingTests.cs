using Xunit;

namespace Stndr.Tests;

public sealed class AnnotationLibrarySortingTests
{
    [Theory]
    [InlineData("2", "000000000002")]
    [InlineData("10", "000000000010")]
    [InlineData("2a.10", "000000000002a.000000000010")]
    [InlineData("Chapter 3.7", "Chapter 000000000003.000000000007")]
    public void Reference_sort_keys_pad_numeric_components(string reference, string expected)
    {
        Assert.Equal(expected, MainWindow.NaturalAnnotationReferenceKey(reference));
    }

    [Fact]
    public void Natural_reference_keys_order_two_before_ten()
    {
        var two = MainWindow.NaturalAnnotationReferenceKey("2a.2");
        var ten = MainWindow.NaturalAnnotationReferenceKey("2a.10");

        Assert.True(string.CompareOrdinal(two, ten) < 0);
    }

    [Theory]
    [InlineData("Genesis 12:3", "Genesis", "בראשית", "בראשית יב:ג")]
    [InlineData("Berakhot 2a:4", "Berakhot", "ברכות", "ברכות ב א:ד")]
    [InlineData("4:10", "Genesis", "בראשית", "בראשית ד:י")]
    public void Hebrew_annotation_references_use_the_hebrew_title_and_numerals(
        string reference,
        string englishTitle,
        string hebrewTitle,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindow.FormatAnnotationHebrewReference(reference, englishTitle, hebrewTitle));
    }
}
