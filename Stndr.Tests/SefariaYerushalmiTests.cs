using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Stndr.Tests;

public sealed class SefariaYerushalmiTests
{
    [Fact]
    public void Vilna_navigation_preserves_canonical_references_and_page_boundaries()
    {
        using var fixture = new YerushalmiFixture();
        var library = fixture.Library;
        var book = fixture.CreateBook("he", fixture.HebrewTextPath);

        var schema = Assert.IsType<BookSchema>(library.GetBookSchema(book.Title));
        Assert.Equal(["Perek", "Halakhah", "Integer"], schema.AddressTypes);
        var vilnaChapter = Assert.Single(schema.AltStructures["Vilna"]);
        Assert.Equal("1a", vilnaChapter.StartingAddress);
        Assert.Equal(["Talmud"], vilnaChapter.AddressTypes);
        Assert.Equal(3, vilnaChapter.Refs.Count);

        var units = library.ReadInstalledBookUnits(book);
        var pages = library.ReadInstalledBookNavigationPages(book);

        Assert.Equal(["1a", "1b", "2a"], pages.Select(page => page.Page));
        Assert.All(pages, page => Assert.Equal("Chapter 1", page.ChapterTitle));
        Assert.Equal(
            ["1.1.1", "1.1.2", "1.1.3", "1.2.1", "1.2.2"],
            units.Select(unit => unit.Reference));
        Assert.Equal("1a", Unit(units, "1.1.1").NavigationKey);
        Assert.Equal("1b", Unit(units, "1.1.2").NavigationKey);
        Assert.Equal("1b", Unit(units, "1.1.3").NavigationKey);
        Assert.Equal("2a", Unit(units, "1.2.1").NavigationKey);
        Assert.Equal("2a", Unit(units, "1.2.2").NavigationKey);
        Assert.Equal(
            "Jerusalem Talmud Test 1:2:1",
            SefariaReferenceFormatting.BuildFullAnchorRef(book.Title, "1.2.1"));
    }

    [Fact]
    public void Hebrew_and_partial_English_versions_share_the_same_Vilna_keys()
    {
        using var fixture = new YerushalmiFixture();
        var hebrew = fixture.Library.ReadInstalledBookUnits(
            fixture.CreateBook("he", fixture.HebrewTextPath));
        var english = fixture.Library.ReadInstalledBookUnits(
            fixture.CreateBook("en", fixture.EnglishTextPath));

        Assert.Equal("1a", Unit(hebrew, "1.1.1").NavigationKey);
        Assert.Equal("1a", Unit(english, "1.1.1").NavigationKey);
        Assert.Equal("2a", Unit(hebrew, "1.2.2").NavigationKey);
        Assert.Equal("2a", Unit(english, "1.2.2").NavigationKey);
        Assert.DoesNotContain(english, unit => unit.Reference == "1.1.3");
    }

    [Fact]
    public void Installed_Berakhot_uses_the_complete_Vilna_page_map_when_available()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var book = library.GetInstalledVersionsForTitle("Jerusalem Talmud Berakhot")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();

        var units = library.ReadInstalledBookUnits(book);
        var pages = library.ReadInstalledBookNavigationPages(book);

        Assert.Equal(655, units.Count);
        Assert.Equal("1a", pages[0].Page);
        Assert.Equal("68a", pages[^1].Page);
        Assert.Equal("1a", Unit(units, "1.1.1").NavigationKey);
        Assert.Equal("68a", Unit(units, "9.5.22").NavigationKey);
        Assert.All(units, unit => Assert.Matches(@"^\d+\.\d+\.\d+$", unit.Reference));
        Assert.All(units, unit => Assert.NotEmpty(unit.NavigationKey));
    }

    [Fact]
    public void Every_installed_Yerushalmi_tractate_maps_all_Hebrew_segments_when_available()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var books = library.GetInstalledBooks()
            .Where(book =>
                SefariaLibraryService.IsHebrew(book) &&
                book.Categories.Count == 3 &&
                book.Categories[0] == "Talmud" &&
                book.Categories[1] == "Yerushalmi" &&
                book.Categories[2].StartsWith("Seder ", StringComparison.Ordinal))
            .GroupBy(book => book.Title, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(book => book.SegmentCount).First())
            .OrderBy(book => book.Title, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(39, books.Count);
        foreach (var book in books)
        {
            var units = library.ReadInstalledBookUnits(book);
            var pages = library.ReadInstalledBookNavigationPages(book);

            Assert.NotEmpty(units);
            Assert.NotEmpty(pages);
            Assert.True(
                pages.Count == pages.Select(page => page.Page).Distinct().Count(),
                $"{book.Title} contains duplicate Vilna page labels: " +
                string.Join(
                    ", ",
                    pages.GroupBy(page => page.Page)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key)));
            Assert.All(units, unit => Assert.Matches(@"^\d+\.\d+\.\d+$", unit.Reference));
            var unmapped = units
                .Where(unit => string.IsNullOrWhiteSpace(unit.NavigationKey))
                .Select(unit => unit.Reference)
                .ToList();
            Assert.True(
                unmapped.Count == 0,
                $"{book.Title} has unmapped references: {string.Join(", ", unmapped)}");
        }
    }

    private static ReaderTextUnit Unit(IEnumerable<ReaderTextUnit> units, string reference) =>
        units.Single(unit => unit.Reference == reference);

    private static string? TryFindDataFolder()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stndr",
                "location.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stndr")
        };

        if (File.Exists(candidates[0]))
        {
            try
            {
                var pointer = System.Text.Json.JsonDocument.Parse(File.ReadAllText(candidates[0]));
                var folder = pointer.RootElement.GetProperty("DataStorageFolder").GetString();
                if (!string.IsNullOrWhiteSpace(folder) &&
                    File.Exists(SefariaOfflineLibraryPaths.ActiveDatabase(folder)))
                {
                    return folder;
                }
            }
            catch
            {
                // Fall through to the conventional folder.
            }
        }

        return File.Exists(SefariaOfflineLibraryPaths.ActiveDatabase(candidates[1]))
            ? candidates[1]
            : null;
    }

    private sealed class YerushalmiFixture : IDisposable
    {
        private const string Title = "Jerusalem Talmud Test";
        private readonly string _folder;

        public YerushalmiFixture()
        {
            _folder = Path.Combine(Path.GetTempPath(), $"stndr-yerushalmi-{Guid.NewGuid():N}");
            Library = new SefariaLibraryService(_folder);
            Directory.CreateDirectory(Library.SchemasFolder);
            File.WriteAllText(Library.GetSchemaFilePath(Title), SchemaJson);
            HebrewTextPath = Path.Combine(_folder, "yerushalmi-he.json");
            EnglishTextPath = Path.Combine(_folder, "yerushalmi-en.json");
            File.WriteAllText(HebrewTextPath, """
                {
                  "text": [
                    [
                      ["he-1", "he-2", "he-3"],
                      ["he-4", "he-5"]
                    ]
                  ]
                }
                """);
            File.WriteAllText(EnglishTextPath, """
                {
                  "text": [
                    [
                      ["en-1", "en-2", ""],
                      ["en-4", "en-5"]
                    ]
                  ]
                }
                """);
        }

        public SefariaLibraryService Library { get; }
        public string HebrewTextPath { get; }
        public string EnglishTextPath { get; }

        public InstalledSefariaBook CreateBook(string language, string path) => new()
        {
            Title = Title,
            LanguageCode = language,
            VersionTitle = $"Test {language}",
            FilePath = path,
            Categories = new List<string>
            {
                "Talmud",
                "Yerushalmi",
                "Seder Zeraim"
            }
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file locking.
            }
        }

        private const string SchemaJson = """
            {
              "title": "Jerusalem Talmud Test",
              "schema": {
                "key": "Jerusalem Talmud Test",
                "nodeType": "JaggedArrayNode",
                "depth": 3,
                "addressTypes": ["Perek", "Halakhah", "Integer"],
                "sectionNames": ["Chapter", "Halakhah", "Segment"]
              },
              "alts": {
                "Vilna": {
                  "nodes": [
                    {
                      "title": "Chapter 1",
                      "heTitle": "פרק א",
                      "wholeRef": "Jerusalem Talmud Test 1:1:1-2:2",
                      "startingAddress": "1a",
                      "addressTypes": ["Talmud"],
                      "sectionNames": ["Daf"],
                      "refs": [
                        "Jerusalem Talmud Test 1:1:1-2",
                        "Jerusalem Talmud Test 1:1:2-2:1",
                        "Jerusalem Talmud Test 1:2:1-2"
                      ]
                    }
                  ]
                }
              }
            }
            """;
    }
}
