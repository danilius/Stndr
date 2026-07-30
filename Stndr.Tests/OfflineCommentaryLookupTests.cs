using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Stndr.Tests;

/// <summary>
/// Integration checks against a local offline library when present.
/// Skips cleanly when the dump is not installed on this machine.
/// </summary>
public sealed class OfflineCommentaryLookupTests
{
    private static string? TryFindDataFolder()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("STNDR_DATA"),
            @"F:\Stndr data",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Stndr")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (SefariaOfflineLibraryInstaller.IsInstalled(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public async Task Berakhot_anchor_returns_commentary_links_with_text()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return; // no offline library in this environment
        }

        var library = new SefariaLibraryService(dataFolder);
        Assert.True(library.HasOfflineLibrary);

        // Must match DB refs exactly: space after title, daf form (no comma).
        var anchor = SefariaReferenceFormatting.BuildFullAnchorRef("Berakhot", "2a.12");
        Assert.Equal("Berakhot 2a:12", anchor);

        var commentaries = await library.GetCommentariesAsync(anchor, CancellationToken.None);
        Assert.NotEmpty(commentaries);
        Assert.Contains(
            commentaries,
            item => item.IndexTitle.Contains("Berakhot", StringComparison.OrdinalIgnoreCase) ||
                    item.Ref.Contains("Berakhot", StringComparison.OrdinalIgnoreCase));

        // Daf-addressed commentaries (Rashi) and numeric ones (Rosh) should both resolve body text.
        Assert.Contains(
            commentaries,
            item => !string.IsNullOrWhiteSpace(item.HebrewText) || !string.IsNullOrWhiteSpace(item.Text));

        var rashi = commentaries.FirstOrDefault(item =>
            item.IndexTitle.Contains("Rashi", StringComparison.OrdinalIgnoreCase));
        if (rashi is not null)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(rashi.HebrewText) && string.IsNullOrWhiteSpace(rashi.Text),
                "Rashi on Berakhot should resolve commentary text for a daf ref.");
        }

        var rosh = commentaries.FirstOrDefault(item =>
            item.IndexTitle.Contains("Rosh", StringComparison.OrdinalIgnoreCase));
        if (rosh is not null)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(rosh.HebrewText) && string.IsNullOrWhiteSpace(rosh.Text),
                "Rosh on Berakhot should resolve commentary text.");
        }
    }

    [Fact]
    public async Task Guide_anchor_returns_commentary_links_with_text()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var anchor = SefariaReferenceFormatting.BuildFullAnchorRef(
            "Guide for the Perplexed",
            "Part 1.default.1.1");
        Assert.Equal("Guide for the Perplexed, Part 1 1:1", anchor);

        var commentaries = await library.GetCommentariesAsync(anchor, CancellationToken.None);
        Assert.NotEmpty(commentaries);

        // At least one commentary should resolve Hebrew or English body text.
        Assert.Contains(
            commentaries,
            item => !string.IsNullOrWhiteSpace(item.HebrewText) ||
                    !string.IsNullOrWhiteSpace(item.Text));
    }

    [Fact]
    public async Task Wrong_comma_form_for_Berakhot_finds_nothing()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        // Regression: the buggy formatter produced this and broke Talmud commentaries.
        var broken = "Berakhot, 2a:12";
        var commentaries = await library.GetCommentariesAsync(broken, CancellationToken.None);
        Assert.Empty(commentaries);
    }
}
