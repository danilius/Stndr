using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using Xunit;

namespace Stndr.Tests;

public sealed class SefariaLibraryOrderingTests
{
    [Fact]
    public void Top_level_house_order_keeps_Kabbalah_last()
    {
        var books = SefariaLibraryOrdering.TopLevelCategories
            .Reverse()
            .Select(category => Book($"A book in {category}", [category]));

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);

        Assert.Equal(SefariaLibraryOrdering.TopLevelCategories, CategoryNames(roots));
    }

    [Fact]
    public void Tanakh_sections_use_house_order_and_books_use_upstream_order()
    {
        var books = new[]
        {
            Book("Deuteronomy", ["Tanakh", "Torah"], order: 5),
            Book("Genesis", ["Tanakh", "Torah"], order: 1),
            Book("Exodus", ["Tanakh", "Torah"], order: 2),
            Book("A modern commentary", ["Tanakh", "Modern Commentary on Tanakh"]),
            Book("A targum", ["Tanakh", "Targum"]),
            Book("A writing", ["Tanakh", "Writings"]),
            Book("A prophet", ["Tanakh", "Prophets"])
        };

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);
        var tanakh = FindCategory(roots, "Tanakh");

        Assert.Equal(
            ["Torah", "Prophets", "Writings", "Targum", "Modern Commentary on Tanakh"],
            CategoryNames(tanakh.Children));
        Assert.Equal(["Genesis", "Exodus", "Deuteronomy"], BookNames(FindCategory(tanakh.Children, "Torah").Children));
    }

    [Fact]
    public void Talmud_second_and_third_levels_use_house_order()
    {
        var sections = new[]
        {
            "Commentary on Minor Tractates",
            "Modern Commentary on Talmud",
            "Acharonim on Talmud",
            "Rishonim on Talmud",
            "Guides",
            "Minor Tractates",
            "Seder Tahorot",
            "Seder Kodashim",
            "Seder Nezikin",
            "Seder Nashim",
            "Seder Moed",
            "Seder Zeraim"
        };
        var books = sections
            .Select(section => Book($"A work in {section}", ["Talmud", "Bavli", section]))
            .Append(Book("Jerusalem Talmud Berakhot", ["Talmud", "Yerushalmi", "Seder Zeraim"]));

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);
        var talmud = FindCategory(roots, "Talmud");
        var bavli = FindCategory(talmud.Children, "Bavli");

        Assert.Equal(["Bavli", "Yerushalmi"], CategoryNames(talmud.Children));
        Assert.Equal(
            [
                "Seder Zeraim", "Seder Moed", "Seder Nashim", "Seder Nezikin", "Seder Kodashim",
                "Seder Tahorot", "Minor Tractates", "Guides", "Rishonim on Talmud",
                "Acharonim on Talmud", "Modern Commentary on Talmud", "Commentary on Minor Tractates"
            ],
            CategoryNames(bavli.Children));
    }

    [Fact]
    public void Halakhah_root_is_reclassified_and_uses_confirmed_house_order()
    {
        var books = new[]
        {
            Book("A Mishneh Torah section", ["Halakhah", "Mishneh Torah"]),
            Book("Shulchan Arukh, Orach Chayim", ["Halakhah", "Shulchan Arukh"]),
            Book("Tur", ["Halakhah", "Tur"]),
            Book("A Rishon", ["Halakhah", "Rishonim"]),
            Book("Acharon commentary", ["Halakhah", "Commentary"]),
            Book("A modern work", ["Halakhah", "Modern"]),
            Book("Sefer HaChinukh", ["Halakhah", "Sifrei Mitzvot"]),
            Book("Halakhot Gedolot", ["Halakhah"]),
            Book("Piskei Recanati", ["Halakhah"]),
            Book("Chokhmat Adam", ["Halakhah"]),
            Book("Chayyei Adam", ["Halakhah"]),
            Book("Kitzur Shulchan Arukh", ["Halakhah"]),
            Book("Arukh HaShulchan", ["Halakhah"]),
            Book("Arukh HaShulchan HeAtid", ["Halakhah"]),
            Book("Ben Ish Hai", ["Halakhah"]),
            Book("Shulchan Arukh HaRav, Orach Chayim", ["Halakhah", "Shulchan Arukh HaRav"]),
            Book("A Future Unclassified Work", ["Halakhah"])
        };

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);
        var halakhah = FindCategory(roots, "Halakhah");

        Assert.Equal(
            ["Mishneh Torah", "Shulchan Arukh", "Tur", "Rishonim", "Acharonim", "Commentary", "Modern", "Sifrei Mitzvot"],
            CategoryNames(halakhah.Children));
        Assert.Equal("A Future Unclassified Work", Assert.Single(BookNames(halakhah.Children)));

        var rishonim = FindCategory(halakhah.Children, "Rishonim");
        Assert.Contains("Halakhot Gedolot", BookNames(rishonim.Children));
        Assert.Contains("Piskei Recanati", BookNames(rishonim.Children));

        var acharonim = FindCategory(halakhah.Children, "Acharonim");
        Assert.Contains("Chokhmat Adam", BookNames(acharonim.Children));
        Assert.Contains("Shulchan Arukh HaRav", CategoryNames(acharonim.Children));
    }

    [Fact]
    public void Core_Halakhah_commentaries_remain_nested_and_sort_after_base_sections()
    {
        var books = new[]
        {
            Book("A base section", ["Halakhah", "Mishneh Torah", "Sefer Madda"], categoryOrders: [0, 0, 10]),
            Book("A commentary", ["Halakhah", "Mishneh Torah", "Commentary"]),
            Book("Tur", ["Halakhah", "Tur"]),
            Book("A Tur commentary", ["Halakhah", "Tur", "Commentary"])
        };

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);
        var halakhah = FindCategory(roots, "Halakhah");
        var mishnehTorah = FindCategory(halakhah.Children, "Mishneh Torah");
        var tur = FindCategory(halakhah.Children, "Tur");

        Assert.Equal(["Sefer Madda", "Commentary"], CategoryNames(mishnehTorah.Children));
        Assert.Equal(["Tur", "Commentary"], NodeNames(tur.Children));
    }

    [Fact]
    public void Kabbalah_is_recursive_categories_first_then_alphabetical()
    {
        var books = new[]
        {
            Book("Zohar work", ["Kabbalah", "Zohar"], order: 1),
            Book("Arizal work", ["Kabbalah", "Arizal and Chaim Vital"], order: 99),
            Book("Beta standalone", ["Kabbalah"], order: 1),
            Book("Alpha standalone", ["Kabbalah"], order: 99),
            Book("Zed child", ["Kabbalah", "Zohar", "Nested Zed"], order: 1),
            Book("Alpha child", ["Kabbalah", "Zohar", "Nested Alpha"], order: 99)
        };

        var roots = SefariaLibraryService.BuildInstalledTreeFromBooks(books);
        var kabbalah = FindCategory(roots, "Kabbalah");

        Assert.Equal(["Arizal and Chaim Vital", "Zohar"], CategoryNames(kabbalah.Children));
        Assert.Equal(["Alpha standalone", "Beta standalone"], BookNames(kabbalah.Children));
        Assert.Equal(
            ["Nested Alpha", "Nested Zed"],
            CategoryNames(FindCategory(kabbalah.Children, "Zohar").Children));
    }

    [Fact]
    public void Imported_work_order_uses_last_numeric_rank()
    {
        var document = new BsonDocument("order", new BsonArray { 34, 8 });

        Assert.Equal(8, SefariaOfflineLibraryImporter.ExtractWorkOrder(document));
        Assert.Equal(3, SefariaOfflineLibraryImporter.ExtractWorkOrder(new BsonDocument("order", 3)));
        Assert.Equal(0, SefariaOfflineLibraryImporter.ExtractWorkOrder(new BsonDocument()));
    }

    private static InstalledSefariaBook Book(
        string title,
        List<string> categories,
        float order = 0,
        List<float>? categoryOrders = null) => new()
        {
            Title = title,
            Categories = categories,
            CategoryOrders = categoryOrders ?? Enumerable.Repeat(0f, categories.Count).ToList(),
            VersionTitle = "Test",
            Order = order
        };

    private static InstalledSefariaCategory FindCategory(IEnumerable<object> nodes, string title) =>
        nodes.OfType<InstalledSefariaCategory>()
            .Single(node => !node.IsBookTitle && string.Equals(node.Title, title, StringComparison.Ordinal));

    private static string[] CategoryNames(IEnumerable<object> nodes) =>
        nodes.OfType<InstalledSefariaCategory>()
            .Where(node => !node.IsBookTitle)
            .Select(node => node.Title)
            .ToArray();

    private static string[] BookNames(IEnumerable<object> nodes) =>
        nodes.OfType<InstalledSefariaCategory>()
            .Where(node => node.IsBookTitle)
            .Select(node => node.Title)
            .ToArray();

    private static string[] NodeNames(IEnumerable<object> nodes) =>
        nodes.OfType<InstalledSefariaCategory>()
            .Select(node => node.Title)
            .ToArray();
}
