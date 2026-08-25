using System;
using Avalonia.Media;
using Xunit;

namespace Stndr.Tests;

public sealed class DictionaryHeadwordCrossReferenceParserTests
{
    [Theory]
    [InlineData("Ch. v. אִיבָּא I .", "אִיבָּא I")]
    [InlineData("cmp. v. also בֵּית אב .", "בֵּית אב")]
    [InlineData("definition; v. זַרְדְּתָא .", "זַרְדְּתָא")]
    public void Finds_hebrew_headword_cross_references(string text, string expectedHeadword)
    {
        var link = Assert.Single(DictionaryHeadwordCrossReferenceParser.Find(text));

        Assert.Equal(expectedHeadword, link.Headword);
        Assert.StartsWith("v.", link.DisplayText);
    }

    [Theory]
    [InlineData("v. supra")]
    [InlineData("v. next w.")]
    [InlineData("v. Pl.")]
    [InlineData("avoid v. in ordinary English")]
    public void Ignores_non_headword_v_references(string text)
    {
        Assert.Empty(DictionaryHeadwordCrossReferenceParser.Find(text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dictionary_text_remains_selectable_with_or_without_links(bool includeLink)
    {
        const string text = "Select this v. אַבָּא reference";
        var links = includeLink
            ? new[]
            {
                new DictionaryTextLink(
                    12,
                    9,
                    "v. אַבָּא",
                    "אַבָּא",
                    "Jastrow Dictionary",
                    DictionaryTextLinkKind.Headword)
            }
            : Array.Empty<DictionaryTextLink>();
        var view = new DictionaryLinkedTextView(
            text,
            links,
            FlowDirection.LeftToRight,
            _ => { });

        view.SelectionStart = 0;
        view.SelectionEnd = 11;

        Assert.Equal("Select this", view.SelectedText);
    }
}
