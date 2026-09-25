using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace Stndr.Tests;

public sealed class SiddurNavigationTests
{
    [Fact]
    public void Schema_order_titles_default_nodes_and_unknown_content_are_preserved()
    {
        using var schemaJson = JsonDocument.Parse("""
        {"key":"Siddur Test","nodes":[{"key":"Shacharit","nodes":[
          {"key":"Shema","titles":[{"lang":"en","text":"Shema &amp; blessings","primary":true}],"depth":1},
          {"key":"Amidah","nodes":[{"key":"default","default":true,"depth":1}]}]}]}
        """);
        using var text = JsonDocument.Parse("""
        {"text":{"Shacharit":{"Amidah":{"default":["first","second"]},"Shema":["opening","שְׁמַע יִשְׂרָאֵל"],"Extra prayer":["retained"]}}}
        """);
        var schema = new BookSchema { Title = "Siddur Test", RootNode = SefariaSchemaNode.Parse(schemaJson.RootElement) };
        Assert.True(SefariaLibraryService.TryReadSiddurUnits(text.RootElement, schema, CancellationToken.None, out var units));
        Assert.Equal(new[] { "Shacharit.Shema.1", "Shacharit.Shema.2", "Shacharit.Amidah.default.1", "Shacharit.Amidah.default.2", "Shacharit.Extra prayer.1" }, units.Select(u => u.Reference));
        Assert.Equal("Shema & blessings", units[0].NavigationLabel);
        Assert.Equal("Shema", units[1].NavigationLabel);
        Assert.Equal("Amidah", units[2].NavigationLabel);
        Assert.DoesNotContain(units.SelectMany(u => u.NavigationPath!), p => p.Title == "default");
        var tree = SiddurNavigationNode.Build(units.Select(u => (u.NavigationKey, u.NavigationPath!)));
        var service = Assert.Single(tree);
        Assert.Equal(3, service.Children.Count);
        Assert.Equal(units[0].NavigationKey, service.TargetKey);
        Assert.Single(service.Children[0].Children);
    }

    [Fact]
    public void Flat_morning_service_gets_subcategories_without_reordering_prayers()
    {
        var names = new[] { "Morning Blessings", "Korbanot", "Hodu", "Baruch She'amar", "Ashrei", "Yishtabach", "Berachos Preceding Shema", "Shema", "Berachos Following Shema", "Amidah" };
        var service = new SefariaSchemaNode { Key = "Shacharit", Title = "Shacharit", Children = names.Select(name => new SefariaSchemaNode { Key = name, Title = name, Depth = 1 }).ToList() };
        var schema = new BookSchema { Title = "Siddur Fixture", RootNode = new SefariaSchemaNode { Key = "Siddur Fixture", Children = new() { service } } };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { text = new Dictionary<string, object> { ["Shacharit"] = names.ToDictionary(name => name, _ => new[] { "prayer text" }) } }));
        Assert.True(SefariaLibraryService.TryReadSiddurUnits(doc.RootElement, schema, CancellationToken.None, out var units));
        Assert.Equal(names.Select(name => "Shacharit." + name + ".1"), units.Select(unit => unit.Reference));
        var root = Assert.Single(SiddurNavigationNode.Build(units.Select(u => (u.NavigationKey, u.NavigationPath!))));
        Assert.Equal(new[] { "Preparatory prayers", "Pesukei d’Zimrah", "Shema and its blessings", "Amidah" }, root.Children.Select(child => child.Part.Title));
        Assert.Equal(4, root.Children[1].Children.Count);
        Assert.Equal("Shacharit.Shema", root.Children[2].Children[1].TargetKey);
    }

    [Theory]
    [InlineData("Pesukei D'Zimra", "<small>Instructions mentioning Ashrei</small><b>אַשְׁרֵי</b> יוֹשְׁבֵי בֵיתֶךָ", "Ashrei")]
    [InlineData("Pesukei Dezimrah", "הַלְלוּיָהּ הַלְלִי נַפְשִׁי", "Psalm 146")]
    [InlineData("Pesukei D'Zimra", "הללויה (תהילים קמ״ו:א׳-ג׳) הללי נפשי את־יהוה", "Psalm 146")]
    [InlineData("Pesukei D'Zimra", "הללויה הללו את שם יהוה", "Psalm 135")]
    [InlineData("Pesukei D'Zimra", "הללויה הללו את יהוה מן השמים", "Psalm 148")]
    [InlineData("The Shema", "<b>שְׁמַע</b> יִשְׂרָאֵל", "Shema")]
    [InlineData("The Shema", "<small>שמע ישראל</small>Instructions", null)]
    [InlineData("Morning Blessings", "שמע ישראל", null)]
    public void Prayer_markers_require_openings_in_relevant_sections(string section, string text, string? expected)
    {
        Assert.Equal(expected, SiddurPrayerMarkers.Find(section, text)?.Title);
    }

    [Fact]
    public void Repeated_prayer_names_in_different_services_have_distinct_targets_and_search_matches()
    {
        var entries = new[]
        {
            ("morning.shema", (IReadOnlyList<ReaderNavigationPart>)new[] { new ReaderNavigationPart("morning", "Shacharit", "שחרית"), new ReaderNavigationPart("morning.shema", "Shema", "שמע") }),
            ("evening.shema", (IReadOnlyList<ReaderNavigationPart>)new[] { new ReaderNavigationPart("evening", "Maariv", "מעריב"), new ReaderNavigationPart("evening.shema", "Shema", "שמע") })
        };
        var tree = SiddurNavigationNode.Build(entries);
        var matches = tree.SelectMany(n => n.DescendantsAndSelf()).Where(n => n.Matches("שְׁמַע")).ToList();
        Assert.Equal(2, matches.Count);
        Assert.Equal(2, matches.Select(n => n.TargetKey).Distinct().Count());
        Assert.All(matches, n => Assert.True(n.Matches("shema")));
    }

    [Theory]
    [InlineData("Siddur Ashkenaz")]
    [InlineData("Siddur Sefard")]
    [InlineData("Siddur Edot HaMizrach")]
    [InlineData("Weekday Siddur Sefard Linear")]
    [InlineData("Shabbat Siddur Sefard Linear")]
    [InlineData("Weekday Siddur Chabad")]
    public void Installed_siddur_versions_have_named_reachable_navigation(string title)
    {
        var folder = Environment.GetEnvironmentVariable("STNDR_DATA") ?? @"F:\Stndr data";
        if (!SefariaOfflineLibraryInstaller.IsInstalled(folder)) return;
        var service = new SefariaLibraryService(folder);
        var versions = service.GetInstalledVersionsForTitle(title);
        Assert.NotEmpty(versions);
        foreach (var version in versions)
        {
            var units = service.ReadInstalledBookUnits(version);
            if (units.Count == 0) continue;
            Assert.All(units, u => { Assert.NotEmpty(u.NavigationKey); Assert.NotEmpty(u.NavigationLabel); Assert.NotNull(u.NavigationPath); });
            Assert.Equal(units.Count, units.Select(u => u.Reference).Distinct().Count());
            Assert.Equal(units.Select(u => u.Reference), service.StreamInstalledBookUnits(version, CancellationToken.None).Select(u => u.Reference));
            var pages = service.ReadInstalledBookNavigationPages(version);
            Assert.All(pages, p => Assert.Contains(units, u => u.NavigationKey == p.Page));
            var tree = SiddurNavigationNode.Build(pages.Select(p => (p.Page, p.NavigationPath!)));
            Assert.NotEmpty(tree);
            Assert.All(tree.SelectMany(n => n.DescendantsAndSelf()), n => Assert.Contains(units, u => u.NavigationKey == n.TargetKey));
        }
        var primary = versions.Where(SefariaLibraryService.IsHebrew).OrderByDescending(v => v.SegmentCount).First();
        var primaryUnits = service.ReadInstalledBookUnits(primary);
        Assert.Contains(primaryUnits, u => u.NavigationLabel.Contains("Shema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(primaryUnits, u => u.NavigationLabel.Contains("Ashrei", StringComparison.OrdinalIgnoreCase));
        if (title is "Siddur Ashkenaz" or "Siddur Sefard" or "Siddur Edot HaMizrach" or "Weekday Siddur Chabad")
            Assert.Contains(primaryUnits, u => u.NavigationLabel == "Psalm 146");
    }
}
