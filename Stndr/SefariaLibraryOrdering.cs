using System;
using System.Collections.Generic;
using System.Linq;

namespace Stndr;

/// <summary>
/// Stable, application-owned ordering and placement rules layered over Sefaria metadata.
/// These rules deliberately survive replacement of the imported Mongo snapshot.
/// </summary>
internal static class SefariaLibraryOrdering
{
    internal const float UnknownOrder = 50_000f;
    private const float UnclassifiedHalakhahOrder = 40_000f;
    private const float CoreCommentaryOrder = 60_000f;

    internal static readonly string[] TopLevelCategories =
    [
        "Tanakh",
        "Mishnah",
        "Talmud",
        "Tosefta",
        "Midrash",
        "Halakhah",
        "Responsa",
        "Liturgy",
        "Jewish Thought",
        "Musar",
        "Chasidut",
        "Second Temple",
        "Reference",
        "Kabbalah"
    ];

    private static readonly string[] TanakhSections =
    [
        "Torah",
        "Prophets",
        "Writings",
        "Targum",
        "Rishonim on Tanakh",
        "Acharonim on Tanakh",
        "Modern Commentary on Tanakh"
    ];

    private static readonly string[] SederCategories =
    [
        "Seder Zeraim",
        "Seder Moed",
        "Seder Nashim",
        "Seder Nezikin",
        "Seder Kodashim",
        "Seder Tahorot"
    ];

    private static readonly string[] MishnahSections =
    [
        .. SederCategories,
        "Rishonim on Mishnah",
        "Acharonim on Mishnah",
        "Modern Commentary on Mishnah"
    ];

    private static readonly string[] TalmudSections = ["Bavli", "Yerushalmi"];

    private static readonly string[] TalmudCorpusSections =
    [
        .. SederCategories,
        "Minor Tractates",
        "Guides",
        "Rishonim on Talmud",
        "Acharonim on Talmud",
        "Modern Commentary on Talmud",
        "Commentary on Minor Tractates",
        "Commentary"
    ];

    private static readonly string[] ToseftaEditions = ["Lieberman Edition", "Vilna Edition"];

    private static readonly string[] ToseftaSections =
    [
        .. SederCategories,
        "Commentary"
    ];

    private static readonly string[] HalakhahSections =
    [
        "Mishneh Torah",
        "Shulchan Arukh",
        "Tur",
        "Rishonim",
        "Acharonim",
        "Commentary",
        "Modern",
        "Sifrei Mitzvot"
    ];

    private static readonly HashSet<string> CoreHalakhahWorks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mishneh Torah",
        "Shulchan Arukh",
        "Tur"
    };

    private static readonly Dictionary<string, string> HalakhahBookBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Halakhot Gedolot"] = "Rishonim",
        ["Piskei Recanati"] = "Rishonim",
        ["Chokhmat Adam"] = "Acharonim",
        ["Chayyei Adam"] = "Acharonim",
        ["Kitzur Shulchan Arukh"] = "Acharonim",
        ["Arukh HaShulchan"] = "Acharonim",
        ["Arukh HaShulchan HeAtid"] = "Acharonim",
        ["Ben Ish Hai"] = "Acharonim"
    };

    private static readonly Dictionary<string, string> HalakhahCategoryBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shulchan Arukh HaRav"] = "Acharonim"
    };

    internal static float GetTopLevelOrder(string? name)
    {
        var index = IndexOf(TopLevelCategories, name);
        if (index >= 0)
        {
            return index;
        }

        return string.Equals(name, "Other", StringComparison.OrdinalIgnoreCase)
            ? UnknownOrder + 1
            : UnknownOrder;
    }

    internal static float GetCategoryOrder(
        IReadOnlyList<string> parentPath,
        string? name,
        float upstreamOrder = 0)
    {
        if (parentPath.Count == 0)
        {
            return GetTopLevelOrder(name);
        }

        if (IsWithin(parentPath, "Kabbalah"))
        {
            return UnknownOrder;
        }

        var houseOrder = GetHouseCategoryOrder(parentPath, name);
        if (houseOrder is not null)
        {
            return houseOrder.Value;
        }

        if (IsPath(parentPath, "Halakhah"))
        {
            return UnclassifiedHalakhahOrder;
        }

        return upstreamOrder > 0 ? upstreamOrder : UnknownOrder;
    }

    internal static float GetBookOrder(
        IReadOnlyList<string> parentPath,
        string? title,
        float upstreamOrder = 0)
    {
        if (IsWithin(parentPath, "Kabbalah"))
        {
            return UnknownOrder;
        }

        if (IsPath(parentPath, "Halakhah"))
        {
            return UnclassifiedHalakhahOrder;
        }

        return upstreamOrder > 0 ? upstreamOrder : UnknownOrder;
    }

    internal static bool CategoriesBeforeBooks(IReadOnlyList<string> parentPath) =>
        IsWithin(parentPath, "Kabbalah");

    internal static void ApplyHalakhahPlacement(InstalledSefariaBook book)
    {
        EnsureMetadataLengths(book);
        if (book.Categories.Count == 0 ||
            !string.Equals(book.Categories[0], "Halakhah", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (book.Categories.Count == 1)
        {
            if (HalakhahBookBuckets.TryGetValue(book.Title, out var bucket))
            {
                InsertCategory(book, 1, bucket);
                // The upstream rank belonged to the Halakhah root, not the new bucket.
                book.Order = 0;
            }

            return;
        }

        var secondLevel = book.Categories[1];
        if (CoreHalakhahWorks.Contains(secondLevel) ||
            IndexOf(HalakhahSections, secondLevel) >= 0)
        {
            return;
        }

        if (HalakhahCategoryBuckets.TryGetValue(secondLevel, out var categoryBucket))
        {
            InsertCategory(book, 1, categoryBucket);
            // The moved category's old root rank is not meaningful within Acharonim.
            book.CategoryOrders[2] = 0;
        }
    }

    private static float? GetHouseCategoryOrder(IReadOnlyList<string> parentPath, string? name)
    {
        string[]? orderedNames = null;
        if (IsPath(parentPath, "Tanakh"))
        {
            orderedNames = TanakhSections;
        }
        else if (IsPath(parentPath, "Mishnah"))
        {
            orderedNames = MishnahSections;
        }
        else if (IsPath(parentPath, "Talmud"))
        {
            orderedNames = TalmudSections;
        }
        else if (IsPath(parentPath, "Talmud", "Bavli") ||
                 IsPath(parentPath, "Talmud", "Yerushalmi"))
        {
            orderedNames = TalmudCorpusSections;
        }
        else if (IsPath(parentPath, "Tosefta"))
        {
            orderedNames = ToseftaEditions;
        }
        else if (parentPath.Count == 2 &&
                 string.Equals(parentPath[0], "Tosefta", StringComparison.OrdinalIgnoreCase))
        {
            orderedNames = ToseftaSections;
        }
        else if (IsPath(parentPath, "Halakhah"))
        {
            orderedNames = HalakhahSections;
        }
        else if (parentPath.Count == 2 &&
                 string.Equals(parentPath[0], "Halakhah", StringComparison.OrdinalIgnoreCase) &&
                 CoreHalakhahWorks.Contains(parentPath[1]) &&
                 string.Equals(name, "Commentary", StringComparison.OrdinalIgnoreCase))
        {
            return CoreCommentaryOrder;
        }

        var index = IndexOf(orderedNames, name);
        return index >= 0 ? index : null;
    }

    private static void EnsureMetadataLengths(InstalledSefariaBook book)
    {
        while (book.HebrewCategories.Count < book.Categories.Count)
        {
            book.HebrewCategories.Add(null);
        }

        while (book.CategoryOrders.Count < book.Categories.Count)
        {
            book.CategoryOrders.Add(0);
        }
    }

    private static void InsertCategory(InstalledSefariaBook book, int index, string category)
    {
        book.Categories.Insert(index, category);
        book.HebrewCategories.Insert(index, null);
        book.CategoryOrders.Insert(index, 0);
    }

    private static int IndexOf(IReadOnlyList<string>? values, string? candidate)
    {
        if (values is null || string.IsNullOrWhiteSpace(candidate))
        {
            return -1;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsWithin(IReadOnlyList<string> path, string root) =>
        path.Count > 0 && string.Equals(path[0], root, StringComparison.OrdinalIgnoreCase);

    private static bool IsPath(IReadOnlyList<string> path, params string[] expected) =>
        path.Count == expected.Length && path.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
}
