using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    internal static bool TryReadSiddurUnits(JsonElement root, BookSchema? schema,
        CancellationToken cancellationToken, out List<ReaderTextUnit> units)
    {
        units = new();
        if (schema?.RootNode is not { Children.Count: > 0 } tree ||
            !Regex.IsMatch(schema.Title + " " + tree.Key, @"\bSiddur\b", RegexOptions.IgnoreCase) ||
            !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.Object)
            return false;

        AppendSiddurNode(text, tree, new(), new(), units, cancellationToken);
        return units.Count > 0;
    }

    private static ReaderNavigationPart? SiddurServiceGroup(SefariaSchemaNode? service, string childKey, string address)
    {
        if (service is null || service.Children.Count < 2) return null;
        var title = SiddurPrayerMarkers.Normalize(FirstSchemaNodeTitle(service));
        var morning = title.Contains("shacharit") || title.Contains("shacharis") || title.Contains("morning");
        var evening = title.Contains("maariv") || title.Contains("arvit");
        if (!morning && !evening) return null;
        var children = service.Children;
        var index = children.FindIndex(child => child.Key == childKey);
        if (index < 0) return null;
        var names = children.Select(child => SiddurPrayerMarkers.Normalize(FirstSchemaNodeTitle(child))).ToList();
        if (morning)
        {
            var start = names.FindIndex(name => name.Contains("pesukei") || name == "hodu" ||
                name == "mizmor shir" || name.StartsWith("baruch she") || name.StartsWith("barukh she"));
            var end = names.FindIndex(name => name == "yishtabach");
            if (start >= 0 && end > start && children.Skip(start).Take(end - start + 1).All(child => child.Children.Count == 0))
            {
                if (index >= start && index <= end)
                    return new(address + "#pesukei", "Pesukei d’Zimrah", "פסוקי דזמרה");
                if (index < start && start > 1)
                    return new(address + "#preparation", "Preparatory prayers", "הכנה לתפילה");
            }
        }
        var firstShema = names.FindIndex(name => name.Contains("shema"));
        var lastShema = names.FindLastIndex(name => name.Contains("shema"));
        if (firstShema >= 0 && lastShema > firstShema && index >= firstShema && index <= lastShema &&
            names.Skip(firstShema).Take(lastShema - firstShema + 1).All(name => name.Contains("shema")))
            return new(address + "#shema", "Shema and its blessings", "קריאת שמע וברכותיה");
        return null;
    }

    private static void AppendSiddurNode(JsonElement text, SefariaSchemaNode? schema,
        List<string> address, List<ReaderNavigationPart> path, List<ReaderTextUnit> units,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (text.ValueKind == JsonValueKind.Object)
        {
            // Schema order is liturgical order; JSON property order is not guaranteed.
            // Retain unrecognised content as well, so a schema update cannot hide prayers.
            var keys = (schema?.Children.Select(child => child.Key) ?? Enumerable.Empty<string>())
                .Concat(text.EnumerateObject().Select(property => property.Name)).Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (!text.TryGetProperty(key, out var childText) || !HasTextContent(childText)) continue;
                var child = schema?.Children.FirstOrDefault(node => node.Key == key);
                var childAddress = new List<string>(address) { key };
                var childPath = new List<ReaderNavigationPart>(path);
                var group = SiddurServiceGroup(schema, key, string.Join('.', address));
                if (group is not null) childPath.Add(group);
                if (child?.IsDefault != true && key != "default")
                    childPath.Add(new(string.Join('.', childAddress),
                        WebUtility.HtmlDecode(child is null ? key : FirstSchemaNodeTitle(child)),
                        WebUtility.HtmlDecode(child?.HeTitle ?? "")));
                AppendSiddurNode(childText, child, childAddress, childPath, units, cancellationToken);
            }
            return;
        }

        if (path.Count == 0) return;
        var leafUnits = EnumerateTextUnits(text, address, cancellationToken).ToList();
        // A day plus service is one visible level, keeping common prayers close.
        if (path.Count > 1 && path[0].Title is "Weekday" or "Shabbat")
        {
            var day = path[0];
            var service = path[1];
            path = new List<ReaderNavigationPart>(path.Skip(1));
            path[0] = service with
            {
                Title = day.Title + " — " + service.Title,
                HebrewTitle = string.IsNullOrWhiteSpace(service.HebrewTitle) ? "" :
                    day.HebrewTitle + " — " + service.HebrewTitle
            };
        }
        var section = path[^1];
        var activePath = path;
        var seenMarkers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in leafUnits)
        {
            var marker = SiddurPrayerMarkers.Find(section.Title, unit.Text);
            if (marker is not null && seenMarkers.Add(marker.Value.Title))
            {
                activePath = new(path)
                {
                    new(section.Key + "#" + marker.Value.Title, marker.Value.Title, marker.Value.HebrewTitle)
                };
            }
            var leaf = activePath[^1];
            units.Add(unit with
            {
                ChapterTitle = string.Join(" / ", activePath.SkipLast(1).Select(part => part.Title)),
                HebrewChapterTitle = string.Join(" / ", activePath.SkipLast(1).Select(part =>
                    string.IsNullOrWhiteSpace(part.HebrewTitle) ? part.Title : part.HebrewTitle)),
                NavigationKey = leaf.Key,
                NavigationLabel = leaf.Title,
                HebrewNavigationLabel = leaf.HebrewTitle,
                NavigationPath = activePath
            });
        }
    }
}

internal static class SiddurPrayerMarkers
{
    // Match prayer openings, never a mention in instructions or a footnote. Only
    // split broad sections: a schema that already names individual prayers wins.
    internal static (string Title, string HebrewTitle)? Find(string section, string html)
    {
        var name = Normalize(section);
        var psalms = name.Contains("pesukei") || name.Contains("zimra");
        var shema = name.Contains("shema");
        var evening = name is "maariv" or "arvit";
        if (!psalms && !shema && !evening) return null;
        var clean = Regex.Replace(html, @"<(small|sup)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        clean = WebUtility.HtmlDecode(Regex.Replace(clean, "<[^>]+>", ""));
        // Some editions embed a source citation between the first words of a psalm.
        clean = Regex.Replace(clean, @"\((?:תהלים|תהילים|Psalms?)\s[^)]*\)", "", RegexOptions.IgnoreCase);
        var opening = Normalize(clean);
        var markers = psalms ? PsalmMarkers : ShemaMarkers;
        foreach (var marker in markers)
        {
            // Psalm 135 shares the first words of Psalm 148.
            if (marker.Title == "Psalm 148" && !opening[..Math.Min(100, opening.Length)].Contains("מן השמים")) continue;
            if (opening.StartsWith(Normalize(marker.Opening), StringComparison.Ordinal) ||
                opening.StartsWith(Normalize(marker.Title) + " ", StringComparison.Ordinal) ||
                opening == Normalize(marker.Title))
                return (marker.Title, marker.HebrewTitle);
        }
        return null;
    }

    internal static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        normalized = Regex.Replace(normalized, @"\p{M}", "");
        return Regex.Replace(normalized.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    private static readonly (string Opening, string Title, string HebrewTitle)[] PsalmMarkers =
    {
        ("למנצח מזמור לדוד השמים", "Psalm 19", "תהלים יט"),
        ("רננו צדיקים", "Psalm 33", "תהלים לג"),
        ("לדוד בשנותו", "Psalm 34", "תהלים לד"),
        ("תפלה למשה", "Psalm 90", "תהלים צ"),
        ("יושב בסתר", "Psalm 91", "תהלים צא"),
        ("מזמור שיר ליום השבת", "Psalm 92", "תהלים צב"),
        ("מזמור שירו", "Psalm 98", "תהלים צח"),
        ("הללויה הללו את שם", "Psalm 135", "תהלים קלה"),
        ("הודו ליהוה כי טוב כי לעולם חסדו", "Psalm 136", "תהלים קלו"),
        ("הודו ליי כי טוב כי לעולם חסדו", "Psalm 136", "תהלים קלו"),
        ("הודו", "Hodu", "הודו"),
        ("מזמור שיר חנכת", "Psalm 30", "מזמור שיר חנוכת הבית"),
        ("ברוך שאמר", "Baruch She’amar", "ברוך שאמר"),
        ("מזמור לתודה", "Psalm 100 — Mizmor LeTodah", "מזמור לתודה"),
        ("יהי כבוד", "Yehi Chevod", "יהי כבוד"),
        ("אשרי יושבי", "Ashrei", "אשרי"),
        ("הללויה הללי נפשי", "Psalm 146", "תהלים קמו"),
        ("הללויה כי טוב", "Psalm 147", "תהלים קמז"),
        ("הללויה הללו את", "Psalm 148", "תהלים קמח"),
        ("הללויה שירו", "Psalm 149", "תהלים קמט"),
        ("הללויה הללו אל", "Psalm 150", "תהלים קנ"),
        ("ויברך דויד", "Vayevarech David", "ויברך דוד"),
        ("ויברך דוד", "Vayevarech David", "ויברך דוד"),
        ("אז ישיר משה", "Az Yashir", "אז ישיר"),
        ("נשמת כל חי", "Nishmat", "נשמת כל חי"),
        ("ובכן ישתבח", "Yishtabach", "ישתבח"),
        ("ישתבח", "Yishtabach", "ישתבח")
    };
    private static readonly (string Opening, string Title, string HebrewTitle)[] ShemaMarkers =
    {
        ("ברכו את", "Barchu", "ברכו"),
        ("אהבה רבה", "Ahavah Rabbah", "אהבה רבה"),
        ("אהבת עולם", "Ahavat Olam", "אהבת עולם"),
        ("אהבת עלם", "Ahavat Olam", "אהבת עולם"),
        ("שמע ישראל", "Shema", "שמע ישראל"),
        ("אמת ויציב", "Emet VeYatziv", "אמת ויציב"),
        ("אמת ואמונה", "Emet VeEmunah", "אמת ואמונה"),
        ("השכיבנו", "Hashkivenu", "השכיבנו")
    };
}
