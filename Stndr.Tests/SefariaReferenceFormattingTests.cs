using Xunit;

namespace Stndr.Tests;

public sealed class SefariaReferenceFormattingTests
{
    [Theory]
    [InlineData("Genesis", "1.1", "Genesis 1:1")]
    [InlineData("Genesis", "1:1", "Genesis 1:1")]
    [InlineData("Berakhot", "2a.12", "Berakhot 2a:12")]
    [InlineData("Berakhot", "2a:12", "Berakhot 2a:12")]
    [InlineData("Berakhot", "30b.5", "Berakhot 30b:5")]
    [InlineData("Berakhot", "2a", "Berakhot 2a")]
    [InlineData("Guide for the Perplexed", "Part 1.default.1.1", "Guide for the Perplexed, Part 1 1:1")]
    [InlineData("Guide for the Perplexed", "Part 1.1.1", "Guide for the Perplexed, Part 1 1:1")]
    [InlineData(
        "Guide for the Perplexed",
        "Introduction of Ibn Tibon.15",
        "Guide for the Perplexed, Introduction of Ibn Tibon 15")]
    public void BuildFullAnchorRef_matches_known_sefaria_shapes(
        string title,
        string unitPath,
        string expected)
    {
        Assert.Equal(expected, SefariaReferenceFormatting.BuildFullAnchorRef(title, unitPath));
    }

    [Theory]
    [InlineData("1:1", false)]
    [InlineData("12", false)]
    [InlineData("2a", false)]
    [InlineData("2a:12", false)]
    [InlineData("30b:5", false)]
    [InlineData("Part 1 1:1", true)]
    [InlineData("Introduction of Ibn Tibon 15", true)]
    public void UsesCommaAfterTitle_distinguishes_daf_from_named_nodes(string relative, bool expectsComma)
    {
        Assert.Equal(expectsComma, SefariaReferenceFormatting.UsesCommaAfterTitle(relative));
    }

    [Fact]
    public void BuildFullAnchorRef_does_not_insert_comma_for_talmud_daf()
    {
        var result = SefariaReferenceFormatting.BuildFullAnchorRef("Berakhot", "2a.12");
        Assert.DoesNotContain(", ", result);
        Assert.Equal("Berakhot 2a:12", result);
    }

    [Fact]
    public void BuildFullAnchorRef_inserts_comma_for_guide_named_nodes()
    {
        var result = SefariaReferenceFormatting.BuildFullAnchorRef(
            "Guide for the Perplexed",
            "Part 1.default.1.1");
        Assert.StartsWith("Guide for the Perplexed, ", result);
        Assert.Equal("Guide for the Perplexed, Part 1 1:1", result);
    }
}
