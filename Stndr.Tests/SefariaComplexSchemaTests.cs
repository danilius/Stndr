using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Stndr.Tests;

public sealed class SefariaComplexSchemaTests
{
    [Fact]
    public void Guide_profile_builds_front_matter_and_chapter_navigation()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var book = library.GetInstalledVersionsForTitle("Guide for the Perplexed")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();
        var units = library.ReadInstalledBookUnits(book);
        var navigation = library.ReadInstalledBookNavigationPages(book);

        Assert.True(units.Count > 1_000);
        Assert.True(navigation.Count > 150);
        Assert.Equal(
            ["Front Matter", "Part 1", "Part 2", "Part 3"],
            navigation.Select(page => page.ChapterTitle).Distinct());
        Assert.Equal(
            ["Letter to R Joseph son of Judah", "Prefatory Remarks", "Part 1.Introduction",
             "Part 1.1", "Part 1.2"],
            navigation.Take(5).Select(page => page.Page));

        var fourthWay = units.Single(unit => unit.Reference == "Part 1.default.75.6");
        Assert.Equal("Part 1.75", fourthWay.NavigationKey);
        Assert.Equal("75", fourthWay.NavigationLabel);
        Assert.StartsWith("<b>", fourthWay.Text);
        Assert.Contains(navigation, page =>
            page.Page == "Part 1.75" &&
            page.Label == "75" &&
            page.ChapterTitle == "Part 1");

        var makbili = library.GetInstalledVersionsForTitle("Guide for the Perplexed")
            .Single(version => version.VersionTitle.StartsWith(
                "Makbili Edition",
                StringComparison.Ordinal));
        var malformedFootnote = library.ReadInstalledBookUnits(makbili)
            .Single(unit => unit.Reference == "Part 2.default.1.5");
        Assert.StartsWith("<i>6</6>", malformedFootnote.Text);

        var sanitized = MainWindow.SanitizeReaderHtmlForWeb(
            malformedFootnote.Text,
            isHebrew: true,
            HebrewMarksMode.NikkudAndCantillation);
        Assert.StartsWith("\u05d3)", sanitized);
        Assert.DoesNotContain("<em>", sanitized);
    }

    [Fact]
    public void Recursive_schema_parser_preserves_named_and_default_nodes()
    {
        using var document = JsonDocument.Parse("""
            {
              "key": "Work",
              "nodeType": "SchemaNode",
              "nodes": [
                {
                  "key": "Genesis",
                  "titles": [
                    { "lang": "en", "text": "Genesis", "primary": true },
                    { "lang": "he", "text": "בראשית", "primary": true }
                  ],
                  "nodes": [
                    {
                      "key": "Introduction",
                      "sharedTitle": "Introduction",
                      "nodeType": "JaggedArrayNode",
                      "depth": 1,
                      "sectionNames": ["Paragraph"],
                      "addressTypes": ["Integer"]
                    },
                    {
                      "key": "default",
                      "default": true,
                      "nodeType": "JaggedArrayNode",
                      "depth": 3,
                      "sectionNames": ["Chapter", "Verse", "Paragraph"],
                      "addressTypes": ["Perek", "Integer", "Integer"]
                    }
                  ]
                }
              ]
            }
            """);

        var root = SefariaSchemaNode.Parse(document.RootElement);

        var genesis = Assert.Single(root.Children);
        Assert.Equal("Genesis", genesis.Key);
        Assert.Equal("Genesis", genesis.Title);
        Assert.Equal("בראשית", genesis.HeTitle);
        Assert.Equal(2, genesis.Children.Count);
        Assert.Equal("Introduction", genesis.Children[0].SharedTitle);
        Assert.True(genesis.Children[1].IsDefault);
        Assert.Equal(["Chapter", "Verse", "Paragraph"], genesis.Children[1].SectionNames);
    }

    [Fact]
    public void Abarbanel_profile_builds_structured_navigation_without_changing_references()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var book = library.GetInstalledVersionsForTitle("Abarbanel on Torah")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();

        var units = library.ReadInstalledBookUnits(book);
        var navigation = library.ReadInstalledBookNavigationPages(book);

        Assert.True(units.Count > 3_000);
        Assert.True(navigation.Count > 400);
        Assert.Equal(
            ["Genesis", "Exodus", "Leviticus", "Numbers", "Deuteronomy"],
            navigation.Select(page => page.ChapterTitle).Distinct());

        var firstGenesisBody = units.First(unit => unit.Reference.StartsWith("Genesis.default.", StringComparison.Ordinal));
        Assert.Equal("Genesis.default.1.1.1", firstGenesisBody.Reference);
        Assert.Equal("Genesis.1.1", firstGenesisBody.NavigationKey);
        Assert.Equal("1:1", firstGenesisBody.NavigationLabel);
        Assert.Equal(
            "Abarbanel on Torah, Genesis 1:1:1",
            SefariaReferenceFormatting.BuildFullAnchorRef(book.Title, firstGenesisBody.Reference));

        var exodusIntroduction = units.FindIndex(unit =>
            unit.Reference.StartsWith("Exodus.Introduction.", StringComparison.Ordinal));
        var exodusBody = units.FindIndex(unit =>
            unit.Reference.StartsWith("Exodus.default.", StringComparison.Ordinal));
        Assert.True(exodusIntroduction >= 0);
        Assert.True(exodusIntroduction < exodusBody);
        Assert.Contains(navigation, page =>
            page.Page == "Exodus.Introduction" && page.Label == "Introduction");
        Assert.Contains(navigation, page =>
            page.Page == "Genesis.1.1" && page.Label == "1:1");
    }

    [Fact]
    public void Existing_Zohar_and_Shulchan_Arukh_navigation_do_not_opt_in()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var zohar = library.GetInstalledVersionsForTitle("Zohar")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();
        var zoharUnits = library.ReadInstalledBookUnits(zohar);
        Assert.NotEmpty(zoharUnits);
        Assert.All(zoharUnits.Take(50), unit => Assert.Empty(unit.NavigationKey));
        Assert.Empty(library.ReadInstalledBookNavigationPages(zohar));

        var shulchanArukh = library.GetInstalledVersionsForTitle("Shulchan Arukh, Even HaEzer")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();
        var shulchanPages = library.ReadInstalledBookNavigationPages(shulchanArukh);
        Assert.True(shulchanPages.Count > 100);
        Assert.All(shulchanPages, page => Assert.Empty(page.Label));
    }

    [Fact]
    public void Smaller_similar_schema_does_not_opt_in_without_regression_coverage()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var cassuto = library.GetInstalledVersionsForTitle("Cassuto on Genesis")
            .Where(SefariaLibraryService.IsHebrew)
            .OrderByDescending(version => version.SegmentCount)
            .First();

        var units = library.ReadInstalledBookUnits(cassuto);

        Assert.NotEmpty(units);
        Assert.All(units.Take(50), unit => Assert.Empty(unit.NavigationKey));
        Assert.Empty(library.ReadInstalledBookNavigationPages(cassuto));
    }

    [Fact]
    public void Tur_composite_selects_substantial_sources_and_builds_four_part_navigation()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var sections = library.GetComplexVersionSections("Tur", "he");

        Assert.Equal(
            ["Orach Chaim", "Yoreh Deah", "Even HaEzer", "Choshen Mishpat"],
            sections.Select(section => section.Key));
        Assert.All(sections, section => Assert.NotEmpty(section.Versions));
        Assert.Equal(
            ["Orach Chaim, Vilna, 1923", "Yoreh Deah, Vilna, 1923",
             "Even HaEzer, Vilna, 1923", "Choshen Mishpat, Vilna, 1923"],
            sections.Select(section => section.Versions[0].VersionTitle));

        var composite = Assert.IsType<InstalledSefariaBook>(
            library.CreateBestAvailableComplexVersion("Tur", "he"));
        Assert.True(composite.IsCompositeOfflineVersion);
        Assert.Equal(4, composite.CompositeSectionVersionIds.Count);

        var units = library.ReadInstalledBookUnits(composite);
        var navigation = library.ReadInstalledBookNavigationPages(composite);

        Assert.True(units.Count > 5_000);
        Assert.True(navigation.Count > 400);
        var navigationGroups = navigation.Select(page => page.ChapterTitle).Distinct().ToList();
        Assert.True(navigationGroups.Count > 20);
        Assert.Contains(navigationGroups, title => title.StartsWith("Orach Chayim — ", StringComparison.Ordinal));
        Assert.Contains(navigationGroups, title => title.StartsWith("Yoreh De'ah — ", StringComparison.Ordinal));
        Assert.Contains(navigationGroups, title => title.StartsWith("Even HaEzer — ", StringComparison.Ordinal));
        Assert.Contains(navigationGroups, title => title.StartsWith("Choshen Mishpat — ", StringComparison.Ordinal));
        Assert.Contains(navigationGroups, title => title.EndsWith("Laws of Tzitzit", StringComparison.Ordinal));
        Assert.Contains(navigation, page =>
            page.Page.StartsWith("Orach Chaim.", StringComparison.Ordinal) &&
            page.Label == "1");
        Assert.Contains(units, unit =>
            unit.Reference.StartsWith("Choshen Mishpat.default.", StringComparison.Ordinal));
    }

    [Fact]
    public void Tur_composite_honours_stable_per_section_preferences()
    {
        var dataFolder = TryFindDataFolder();
        if (dataFolder is null)
        {
            return;
        }

        var library = new SefariaLibraryService(dataFolder);
        var sections = library.GetComplexVersionSections("Tur", "he");
        var yorehDeah = sections.Single(section => section.Key == "Yoreh Deah");
        var warsaw = yorehDeah.Versions.Single(version => version.VersionTitle == "Warsaw 1861");
        var preferred = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Yoreh Deah"] = warsaw.StableVersionKey
        };

        var composite = Assert.IsType<InstalledSefariaBook>(
            library.CreateBestAvailableComplexVersion("Tur", "he", preferred));

        Assert.Equal(
            warsaw.OfflineVersionId,
            composite.CompositeSectionVersionIds["Yoreh Deah"]);
        Assert.NotEqual(
            warsaw.OfflineVersionId,
            composite.CompositeSectionVersionIds["Choshen Mishpat"]);
    }

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

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            SefariaOfflineLibraryInstaller.IsInstalled(candidate));
    }
}
