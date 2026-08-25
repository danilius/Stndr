using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Stndr.Tests;

public sealed class OfflineDictionaryBrowsingTests
{
    [Fact]
    public async Task Dictionary_entries_can_be_opened_with_ordered_context()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var lexicon = (await library.GetOfflineLexiconsAsync())
            .FirstOrDefault(item => item.EntryCount >= 5);
        Assert.NotNull(lexicon);

        var firstEntries = await library.BrowseOfflineDictionaryAsync(lexicon!.Id, "", 0, 5);
        Assert.True(firstEntries.Count >= 3);
        Assert.All(firstEntries, entry => Assert.Equal(lexicon.Id, entry.LexiconId));

        var target = firstEntries[2];
        var context = await library.GetOfflineDictionaryContextAsync(target.EntryId, 2, 2);
        Assert.Contains(context, entry => entry.EntryId == target.EntryId);
        Assert.All(context, entry => Assert.Equal(lexicon.Id, entry.LexiconId));
    }

    [Fact]
    public async Task Headword_search_can_be_limited_to_one_dictionary()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var lexicon = (await library.GetOfflineLexiconsAsync())
            .FirstOrDefault(item => item.EntryCount > 0);
        Assert.NotNull(lexicon);

        var entry = (await library.BrowseOfflineDictionaryAsync(lexicon!.Id, "", 0, 1)).Single();
        var matches = await library.SearchOfflineDictionaryAsync(
            entry.Headword,
            lexicon.Id,
            20,
            mode: SefariaDictionarySearchMode.Headwords);

        Assert.Contains(matches, match => match.EntryId == entry.EntryId);
        Assert.All(matches, match => Assert.Equal(lexicon.Id, match.LexiconId));
    }

    [Fact]
    public async Task Entry_text_search_finds_words_inside_definitions()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var lexicon = (await library.GetOfflineLexiconsAsync())
            .FirstOrDefault(item => item.Name.Contains("BDB Aramaic", StringComparison.OrdinalIgnoreCase));
        if (lexicon is null)
        {
            return;
        }

        var matches = await library.SearchOfflineDictionaryAsync(
            "father",
            lexicon.Id,
            20,
            mode: SefariaDictionarySearchMode.EntryText);

        Assert.Contains(matches, match =>
            match.Definition.Contains("father", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Jastrow_cross_reference_headword_resolves_in_the_same_dictionary()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var jastrow = (await library.GetOfflineLexiconsAsync())
            .FirstOrDefault(item => item.Name.Contains("Jastrow", StringComparison.OrdinalIgnoreCase));
        if (jastrow is null)
        {
            return;
        }

        var matches = await library.SearchOfflineDictionaryAsync(
            "אַוָּארָא",
            jastrow.Id,
            20,
            mode: SefariaDictionarySearchMode.Headwords);

        Assert.NotEmpty(matches);
        Assert.All(matches, match => Assert.Equal(jastrow.Id, match.LexiconId));
    }

    [Fact]
    public async Task Jastrow_entry_text_is_searchable_within_the_dictionary()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var jastrow = (await library.GetOfflineLexiconsAsync())
            .FirstOrDefault(item => item.Name.Contains("Jastrow", StringComparison.OrdinalIgnoreCase));
        if (jastrow is null)
        {
            return;
        }

        var searchTerm = (await library.BrowseOfflineDictionaryAsync(jastrow.Id, "", 0, 100))
            .SelectMany(entry => Regex.Matches(entry.Definition, "[A-Za-z]{8,}")
                .Select(match => match.Value))
            .OrderByDescending(term => term.Length)
            .FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(searchTerm));

        var matches = await library.SearchOfflineDictionaryAsync(
            searchTerm!,
            jastrow.Id,
            100,
            mode: SefariaDictionarySearchMode.EntryText);

        Assert.Contains(matches, match =>
            match.Definition.Contains(searchTerm!, StringComparison.OrdinalIgnoreCase));
        Assert.All(matches, match => Assert.Equal(jastrow.Id, match.LexiconId));
    }

    private static string? TryFindDataFolder()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("STNDR_DATA"),
            @"F:\Stndr data",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stndr")
        };

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && SefariaOfflineLibraryInstaller.IsInstalled(candidate));
    }
}
