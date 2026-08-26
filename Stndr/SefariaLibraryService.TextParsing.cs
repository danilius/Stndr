using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteDatabase = LiteDB.LiteDatabase;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public string ReadInstalledBookText(InstalledSefariaBook book)
    {
        var json = ReadBookJson(book);
        using var document = JsonDocument.Parse(json);
        if (!TryGetPrimaryTextElement(document.RootElement, out var textElement))
        {
            return json;
        }

        var lines = new List<string>();
        AppendTextElement(textElement, lines, 1);
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    public List<ReaderTextUnit> ReadInstalledBookUnits(InstalledSefariaBook book)
    {
        return ReadInstalledBookUnits(book, CancellationToken.None);
    }

    public List<ReaderTextUnit> ReadInstalledBookUnits(InstalledSefariaBook book, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = ReadBookJson(book);
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        cancellationToken.ThrowIfCancellationRequested();
        var root = document.RootElement;
        var schema = GetBookSchema(book.Title);
        if (TryReadYerushalmiVilnaTextUnits(
                book,
                root,
                schema,
                cancellationToken,
                out var yerushalmiUnits))
        {
            return yerushalmiUnits;
        }

        // Tractates and Talmud-addressed commentaries (Rashi on Berakhot, etc.) use daf labels.
        if (UsesTalmudDafAddressing(book, schema))
        {
            return ReadTalmudTextUnits(book, root, schema, cancellationToken);
        }

        if (TryReadSupportedComplexSchemaUnits(root, schema, cancellationToken, out var structuredUnits))
        {
            return structuredUnits;
        }

        if (!TryGetPrimaryTextElement(root, out var textElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new List<ReaderTextUnit>
            {
                new("1", json)
            };
        }

        var units = new List<ReaderTextUnit>();
        if (IsMishnah(book))
        {
            AppendMishnahTextUnits(textElement, units, cancellationToken);
            return units;
        }

        AppendTextUnits(textElement, units, new List<string>(), cancellationToken);
        return units;
    }

    public IEnumerable<ReaderTextUnit> StreamInstalledBookUnits(
        InstalledSefariaBook book,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = ReadBookJson(book);
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json);
        cancellationToken.ThrowIfCancellationRequested();

        var root = document.RootElement;
        var schema = GetBookSchema(book.Title);
        if (TryReadYerushalmiVilnaTextUnits(
                book,
                root,
                schema,
                cancellationToken,
                out var yerushalmiUnits))
        {
            foreach (var unit in yerushalmiUnits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return unit;
            }

            yield break;
        }

        if (UsesTalmudDafAddressing(book, schema))
        {
            foreach (var unit in EnumerateTalmudTextUnits(book, root, schema, cancellationToken))
            {
                yield return unit;
            }

            yield break;
        }

        if (TryReadSupportedComplexSchemaUnits(root, schema, cancellationToken, out var structuredUnits))
        {
            foreach (var unit in structuredUnits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return unit;
            }

            yield break;
        }

        if (!TryGetPrimaryTextElement(root, out var textElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ReaderTextUnit("1", json);
            yield break;
        }

        var units = IsMishnah(book)
            ? EnumerateMishnahTextUnits(textElement, cancellationToken)
            : EnumerateTextUnits(textElement, new List<string>(), cancellationToken);
        foreach (var unit in units)
        {
            yield return unit;
        }
    }

    public List<ReaderNavigationPage> ReadInstalledBookNavigationPages(InstalledSefariaBook book)
    {
        var json = ReadBookJson(book);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var schema = GetBookSchema(book.Title);

        if (IsShulchanArukh(book))
        {
            var shulchanPages = ReadShulchanArukhNavigationPages(book, root);
            if (shulchanPages.Count > 0)
            {
                return shulchanPages;
            }
        }

        if (TryBuildYerushalmiVilnaNavigation(
                book,
                schema,
                out var yerushalmiPages,
                out _))
        {
            return yerushalmiPages;
        }

        if (TryReadSupportedComplexSchemaUnits(root, schema, CancellationToken.None, out var structuredUnits))
        {
            return structuredUnits
                .Where(unit => !string.IsNullOrWhiteSpace(unit.NavigationKey))
                .GroupBy(unit => unit.NavigationKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(unit => new ReaderNavigationPage(
                    unit.NavigationKey,
                    unit.ChapterTitle,
                    unit.HebrewChapterTitle,
                    unit.NavigationLabel,
                    unit.HebrewNavigationLabel))
                .ToList();
        }

        if (!IsTalmud(book) ||
            !root.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return ReadDirectTalmudNavigationPages(book, root, schema);
        }

        var navigationPages = new List<ReaderNavigationPage>();
        var chapterTitle = string.Empty;
        var hebrewChapterTitle = string.Empty;
        foreach (var pageRoot in pages.EnumerateArray())
        {
            var page = GetTalmudPage(pageRoot);
            if (string.IsNullOrWhiteSpace(page))
            {
                continue;
            }

            var (schTitle, schHe) = GetChapterTitleFromSchema(schema, page);
            if (!string.IsNullOrWhiteSpace(schTitle) || !string.IsNullOrWhiteSpace(schHe))
            {
                chapterTitle = schTitle;
                hebrewChapterTitle = schHe;
            }
            else
            {
                var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
                if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                    !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
                {
                    chapterTitle = pageChapterTitles.ChapterTitle;
                    hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
                }
            }

            navigationPages.Add(new ReaderNavigationPage(page, chapterTitle, hebrewChapterTitle));
        }

        return navigationPages;
    }

    private List<ReaderNavigationPage> ReadShulchanArukhNavigationPages(InstalledSefariaBook book, JsonElement root)
    {
        var schema = GetBookSchema(book.Title);
        if (schema is null ||
            !schema.AltStructures.TryGetValue("Topic", out var topics) ||
            topics.Count == 0)
        {
            return new List<ReaderNavigationPage>();
        }

        if (!TryGetPrimaryTextElement(root, out var textElement) ||
            textElement.ValueKind != JsonValueKind.Array)
        {
            return new List<ReaderNavigationPage>();
        }

        var navigationPages = new List<ReaderNavigationPage>();
        var siman = 1;
        foreach (var chapterElement in textElement.EnumerateArray())
        {
            var label = siman.ToString();
            var (chapterTitle, hebrewChapterTitle) = GetTopicTitleFromSchema(topics, label);
            if (string.IsNullOrWhiteSpace(chapterTitle) && string.IsNullOrWhiteSpace(hebrewChapterTitle))
            {
                var localHeading = ExtractSimanHeadingFromChapter(chapterElement);
                if (!string.IsNullOrWhiteSpace(localHeading))
                {
                    hebrewChapterTitle = localHeading;
                }
            }

            navigationPages.Add(new ReaderNavigationPage(label, chapterTitle, hebrewChapterTitle));
            siman++;
        }

        return navigationPages;
    }

    private static List<ReaderNavigationPage> ReadDirectTalmudNavigationPages(InstalledSefariaBook book, JsonElement root, BookSchema? schema = null)
    {
        if (!UsesTalmudDafAddressing(book, schema) ||
            !root.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.Array ||
            !textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            return new List<ReaderNavigationPage>();
        }

        var navigationPages = new List<ReaderNavigationPage>();
        var address = 0;
        foreach (var _ in textElement.EnumerateArray())
        {
            var pageLabel = FormatTalmudPageFromAddress(address);
            var (chapterTitle, hebrewChapterTitle) = GetChapterTitleFromSchema(schema, pageLabel);
            navigationPages.Add(new ReaderNavigationPage(pageLabel, chapterTitle, hebrewChapterTitle));
            address++;
        }

        return navigationPages;
    }

    private sealed record YerushalmiNavigationRange(
        string Page,
        string ChapterTitle,
        string HebrewChapterTitle,
        int[] Start,
        int[] End);

    private static bool TryReadYerushalmiVilnaTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        BookSchema? schema,
        CancellationToken cancellationToken,
        out List<ReaderTextUnit> units)
    {
        units = new List<ReaderTextUnit>();
        if (!TryBuildYerushalmiVilnaNavigation(book, schema, out _, out var ranges) ||
            !TryGetPrimaryTextElement(root, out var textElement))
        {
            return false;
        }

        foreach (var unit in EnumerateTextUnits(
                     textElement,
                     new List<string>(),
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseYerushalmiAddress(unit.Reference, out var address))
            {
                units.Add(unit);
                continue;
            }

            // Sefaria page ranges intentionally overlap at segments that cross a page
            // boundary. Anchor the shared segment to the later page so every page starts
            // at the segment containing its first words.
            var range = ranges.LastOrDefault(candidate =>
                CompareYerushalmiAddresses(address, candidate.Start) >= 0 &&
                CompareYerushalmiAddresses(address, candidate.End) <= 0);
            // A few source texts contain canonical segments omitted from Sefaria's
            // alternate pagination map (for example, Peah 3:8:1-3). Keep those
            // segments visible on the most recent preceding page.
            range ??= ranges.LastOrDefault(candidate =>
                CompareYerushalmiAddresses(address, candidate.Start) >= 0);
            range ??= ranges.FirstOrDefault();
            units.Add(range is null
                ? unit
                : unit with
                {
                    ChapterTitle = range.ChapterTitle,
                    HebrewChapterTitle = range.HebrewChapterTitle,
                    NavigationKey = range.Page
                });
        }

        return units.Count > 0;
    }

    private static bool TryBuildYerushalmiVilnaNavigation(
        InstalledSefariaBook book,
        BookSchema? schema,
        out List<ReaderNavigationPage> pages,
        out List<YerushalmiNavigationRange> ranges)
    {
        pages = new List<ReaderNavigationPage>();
        ranges = new List<YerushalmiNavigationRange>();
        if (!IsYerushalmi(book) ||
            schema is null ||
            !schema.AltStructures.TryGetValue("Vilna", out var chapters))
        {
            return false;
        }

        foreach (var chapter in chapters)
        {
            if (chapter.Refs.Count == 0 ||
                chapter.AddressTypes.Count == 0 ||
                !string.Equals(
                    chapter.AddressTypes[0],
                    "Talmud",
                    StringComparison.OrdinalIgnoreCase) ||
                !TryParseTalmudPageAddress(chapter.StartingAddress, out var pageAddress))
            {
                continue;
            }

            foreach (var referenceRange in chapter.Refs)
            {
                if (TryParseYerushalmiReferenceRange(referenceRange, out var start, out var end))
                {
                    var page = FormatTalmudPageFromAddress(pageAddress);
                    if (!pages.Any(candidate =>
                            string.Equals(candidate.Page, page, StringComparison.Ordinal)))
                    {
                        pages.Add(new ReaderNavigationPage(
                            page,
                            chapter.Title,
                            chapter.HeTitle));
                    }

                    ranges.Add(new YerushalmiNavigationRange(
                        page,
                        chapter.Title,
                        chapter.HeTitle,
                        start,
                        end));
                }

                pageAddress++;
            }
        }

        return ranges.Count > 0;
    }

    private static bool TryParseTalmudPageAddress(string page, out int address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(page))
        {
            return false;
        }

        var normalized = page.Trim().ToLowerInvariant();
        var digitCount = 0;
        while (digitCount < normalized.Length && char.IsDigit(normalized[digitCount]))
        {
            digitCount++;
        }

        if (digitCount == 0 ||
            digitCount >= normalized.Length ||
            !int.TryParse(normalized[..digitCount], out var daf) ||
            daf <= 0 ||
            normalized[digitCount] is not ('a' or 'b'))
        {
            return false;
        }

        address = (daf - 1) * 2 + (normalized[digitCount] == 'b' ? 1 : 0);
        return true;
    }

    private static bool TryParseYerushalmiReferenceRange(
        string referenceRange,
        out int[] start,
        out int[] end)
    {
        start = Array.Empty<int>();
        end = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(referenceRange))
        {
            return false;
        }

        var lastSpace = referenceRange.LastIndexOf(' ');
        var addressRange = lastSpace >= 0
            ? referenceRange[(lastSpace + 1)..]
            : referenceRange;
        var separator = addressRange.IndexOf('-');
        var startText = separator >= 0 ? addressRange[..separator] : addressRange;
        var endText = separator >= 0 ? addressRange[(separator + 1)..] : startText;
        if (!TryParseYerushalmiAddress(startText, out start) ||
            !TryParseYerushalmiAddress(endText, out var abbreviatedEnd))
        {
            return false;
        }

        if (abbreviatedEnd.Length > start.Length)
        {
            return false;
        }

        end = new int[start.Length];
        var retainedParts = start.Length - abbreviatedEnd.Length;
        Array.Copy(start, end, retainedParts);
        Array.Copy(abbreviatedEnd, 0, end, retainedParts, abbreviatedEnd.Length);
        return CompareYerushalmiAddresses(start, end) <= 0;
    }

    private static bool TryParseYerushalmiAddress(string address, out int[] parts)
    {
        parts = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var rawParts = address.Split(
            new[] { ':', '.' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawParts.Length == 0)
        {
            return false;
        }

        var parsed = new int[rawParts.Length];
        for (var index = 0; index < rawParts.Length; index++)
        {
            if (!int.TryParse(rawParts[index], out parsed[index]) || parsed[index] <= 0)
            {
                return false;
            }
        }

        parts = parsed;
        return true;
    }

    private static int CompareYerushalmiAddresses(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var length = Math.Max(left.Count, right.Count);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < left.Count ? left[index] : 0;
            var rightPart = index < right.Count ? right[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static (string ChapterTitle, string HebrewChapterTitle) GetChapterTitleFromSchema(BookSchema? schema, string page)
    {
        if (schema != null && schema.AltStructures.TryGetValue("Chapters", out var chs) && chs.Count > 0)
        {
            double current = ToDafNumber(page);
            SchemaAltNode? best = null;
            double bestStart = -1;
            foreach (var node in chs)
            {
                string startStr = ExtractStartDaf(node.WholeRef);
                double start = ToDafNumber(startStr);
                if (current >= start && start > bestStart)
                {
                    bestStart = start;
                    best = node;
                }
            }
            if (best != null)
            {
                return (best.Title, best.HeTitle);
            }
            var last = chs[^1];
            return (last.Title, last.HeTitle);
        }
        return ("", "");
    }

    private static (string ChapterTitle, string HebrewChapterTitle) GetTopicTitleFromSchema(
        IReadOnlyList<SchemaAltNode> topics,
        string section,
        string? divisionTitle = null)
    {
        if (!int.TryParse(section, out var sectionNumber))
        {
            return (string.Empty, string.Empty);
        }

        foreach (var topic in topics)
        {
            if ((string.IsNullOrWhiteSpace(divisionTitle) ||
                 topic.WholeRef.Contains($", {divisionTitle} ", StringComparison.OrdinalIgnoreCase)) &&
                TryParseWholeRefSimanRange(topic.WholeRef, out var start, out var end) &&
                sectionNumber >= start &&
                sectionNumber <= end)
            {
                return (topic.Title.Trim(), topic.HeTitle.Trim());
            }
        }

        return (string.Empty, string.Empty);
    }

    private static bool TryParseWholeRefSimanRange(string wholeRef, out int simanStart, out int simanEnd)
    {
        simanStart = 0;
        simanEnd = 0;
        if (string.IsNullOrWhiteSpace(wholeRef))
        {
            return false;
        }

        var address = wholeRef;
        var lastSpace = address.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            address = address[(lastSpace + 1)..];
        }

        if (address.Contains(':', StringComparison.Ordinal))
        {
            var simanim = ExtractSimanNumbersFromAddress(address);
            if (simanim.Count == 0)
            {
                return false;
            }

            simanStart = simanim[0];
            simanEnd = simanim[^1];
            return true;
        }

        var dash = address.IndexOf('-');
        if (dash >= 0)
        {
            if (!int.TryParse(address[..dash], out simanStart) ||
                !int.TryParse(address[(dash + 1)..], out simanEnd))
            {
                return false;
            }

            return true;
        }

        if (!int.TryParse(address, out simanStart))
        {
            return false;
        }

        simanEnd = simanStart;
        return true;
    }

    private static List<int> ExtractSimanNumbersFromAddress(string address)
    {
        var simanim = new List<int>();
        for (var index = 0; index < address.Length; index++)
        {
            if (!char.IsDigit(address[index]))
            {
                continue;
            }

            var start = index;
            while (index < address.Length && char.IsDigit(address[index]))
            {
                index++;
            }

            if (index < address.Length && address[index] == ':')
            {
                if (int.TryParse(address[start..index], out var siman))
                {
                    simanim.Add(siman);
                }
            }
        }

        return simanim;
    }

    private static string ExtractSimanHeadingFromChapter(JsonElement chapter)
    {
        if (chapter.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var item in chapter.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var boldStart = text.IndexOf("<b>", StringComparison.OrdinalIgnoreCase);
            if (boldStart < 0)
            {
                continue;
            }

            var boldEnd = text.IndexOf("</b>", boldStart, StringComparison.OrdinalIgnoreCase);
            if (boldEnd < 0)
            {
                continue;
            }

            var heading = RemoveSmallTagsWithContent(text.Substring(boldStart + 3, boldEnd - boldStart - 3));
            heading = CollapseWhitespace(heading);
            var periodIndex = heading.IndexOf('.');
            if (periodIndex > 0)
            {
                heading = heading[..periodIndex].Trim();
            }

            return heading;
        }

        return string.Empty;
    }

    private static double ToDafNumber(string daf)
    {
        if (string.IsNullOrWhiteSpace(daf)) return 0;
        daf = daf.ToLowerInvariant().Trim();
        int i = 0;
        while (i < daf.Length && char.IsDigit(daf[i])) i++;
        if (i == 0) return 0;
        if (!int.TryParse(daf.Substring(0, i), out int num)) return 0;
        double val = num;
        if (i < daf.Length && daf[i] == 'b') val += 0.5;
        return val;
    }

    private static string ExtractStartDaf(string wholeRef)
    {
        if (string.IsNullOrWhiteSpace(wholeRef)) return "";
        string s = wholeRef;
        // Use LAST space to skip multi-word titles like "Rosh Hashanah"
        int sp = s.LastIndexOf(' ');
        if (sp >= 0) s = s.Substring(sp + 1);
        int d = s.IndexOf('-');
        if (d >= 0) s = s.Substring(0, d);
        int c = s.IndexOf(':');
        if (c >= 0) s = s.Substring(0, c);
        return s.Trim();
    }

    public static bool IsHebrew(InstalledSefariaBook book)
    {
        return string.Equals(book.LanguageCode, "he", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMishnah(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Mishnah", StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsesTalmudDafAddressing(
        InstalledSefariaBook book,
        BookSchema? schema)
    {
        if (schema?.AddressTypes.Count > 0)
        {
            return schema.HasTalmudDafAddressing;
        }

        return IsTalmud(book);
    }

    private static bool IsYerushalmi(InstalledSefariaBook book)
    {
        return book.Categories.Count == 3 &&
            string.Equals(book.Categories[0], "Talmud", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(book.Categories[1], "Yerushalmi", StringComparison.OrdinalIgnoreCase) &&
            book.Categories[2].StartsWith("Seder ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTalmud(InstalledSefariaBook book)
    {
        // Only daf-paginated tractates under Talmud. Guides, commentaries, and other works live
        // under Talmud in the TOC but use ordinary (non-Talmud) text shapes.
        if (!book.Categories.Any(category => string.Equals(category, "Talmud", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (book.Categories.Any(category =>
                string.Equals(category, "Guides", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "Commentary", StringComparison.OrdinalIgnoreCase) ||
                category.Contains(" on ", StringComparison.OrdinalIgnoreCase) ||
                category.Contains("Introduction", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Tractates sit under a Seder; Guides do not.
        return book.Categories.Any(category =>
            category.StartsWith("Seder ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsShulchanArukh(InstalledSefariaBook book)
    {
        return book.Categories.Any(category =>
            string.Equals(category, "Shulchan Arukh", StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendTextElement(JsonElement element, List<string> lines, int chapterNumber)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                AppendTextElement(property.Value, lines, chapterNumber);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var containsNestedArrays = element.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array);
        if (!containsNestedArrays)
        {
            var segmentNumber = 1;
            foreach (var item in element.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add($"{chapterNumber}:{segmentNumber}  {text}");
                }

                segmentNumber++;
            }

            return;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            AppendTextElement(item, lines, index);
            index++;
        }
    }

    private static void AppendTextUnits(
        JsonElement element,
        List<ReaderTextUnit> units,
        List<string> path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                units.Add(new ReaderTextUnit(string.Join(".", path), text));
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextPath = new List<string>(path) { property.Name };
                AppendTextUnits(property.Value, units, nextPath, cancellationToken);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextPath = new List<string>(path) { index.ToString() };
            AppendTextUnits(item, units, nextPath, cancellationToken);
            index++;
        }
    }

    private static bool TryReadSupportedComplexSchemaUnits(
        JsonElement root,
        BookSchema? schema,
        CancellationToken cancellationToken,
        out List<ReaderTextUnit> units)
    {
        units = new List<ReaderTextUnit>();
        if (TryReadNamedSimanSeifProfile(root, schema, cancellationToken, out units))
        {
            return true;
        }

        // Require a genuine multi-part commentary. A two-node work can share this
        // shape while having different navigation expectations; keep those on the
        // established fallback until they have their own regression coverage.
        if (schema?.RootNode is not { Children.Count: >= 3 } schemaRoot ||
            !root.TryGetProperty("text", out var rawText) ||
            rawText.ValueKind != JsonValueKind.Object ||
            !TryMatchNamedChapterVerseProfile(schemaRoot, rawText, out var containers))
        {
            return false;
        }

        foreach (var (container, introduction, body, content) in containers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupTitle = FirstSchemaNodeTitle(container);
            var hebrewGroupTitle = container.HeTitle;

            if (content.TryGetProperty(introduction.Key, out var introductionText) &&
                HasTextContent(introductionText))
            {
                var introductionTitle = FirstSchemaNodeTitle(introduction);
                var hebrewIntroductionTitle = introduction.HeTitle;
                if (string.IsNullOrWhiteSpace(hebrewIntroductionTitle) &&
                    string.Equals(introductionTitle, "Introduction", StringComparison.OrdinalIgnoreCase))
                {
                    hebrewIntroductionTitle = "\u05d4\u05e7\u05d3\u05de\u05d4";
                }

                foreach (var unit in EnumerateTextUnits(
                             introductionText,
                             new List<string> { container.Key, introduction.Key },
                             cancellationToken))
                {
                    units.Add(unit with
                    {
                        ChapterTitle = groupTitle,
                        HebrewChapterTitle = hebrewGroupTitle,
                        NavigationKey = $"{container.Key}.{introduction.Key}",
                        NavigationLabel = introductionTitle,
                        HebrewNavigationLabel = hebrewIntroductionTitle
                    });
                }
            }

            if (!content.TryGetProperty(body.Key, out var bodyText) || !HasTextContent(bodyText))
            {
                continue;
            }

            foreach (var unit in EnumerateTextUnits(
                         bodyText,
                         new List<string> { container.Key, body.Key },
                         cancellationToken))
            {
                var parts = unit.Reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 5)
                {
                    // This profile promises Chapter/Verse/Paragraph. Falling back is safer than
                    // emitting partially addressed rows if a future dump changes that contract.
                    units.Clear();
                    return false;
                }

                var chapter = parts[2];
                var verse = parts[3];
                units.Add(unit with
                {
                    ChapterTitle = groupTitle,
                    HebrewChapterTitle = hebrewGroupTitle,
                    NavigationKey = $"{container.Key}.{chapter}.{verse}",
                    NavigationLabel = $"{chapter}:{verse}"
                });
            }
        }

        return units.Count > 0;
    }

    private static bool TryReadNamedSimanSeifProfile(
        JsonElement root,
        BookSchema? schema,
        CancellationToken cancellationToken,
        out List<ReaderTextUnit> units)
    {
        units = new List<ReaderTextUnit>();
        if (schema?.RootNode is not { Children.Count: >= 2 } schemaRoot ||
            !root.TryGetProperty("text", out var rawText) ||
            rawText.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var schemaKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in schemaRoot.Children)
        {
            var body = container.Children.FirstOrDefault(node =>
                node.IsDefault &&
                node.Children.Count == 0 &&
                node.Depth == 2 &&
                node.SectionNames.Count == 2 &&
                string.Equals(node.SectionNames[0], "Siman", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.SectionNames[1], "Seif", StringComparison.OrdinalIgnoreCase));
            if (body is null ||
                string.IsNullOrWhiteSpace(container.Key) ||
                !rawText.TryGetProperty(container.Key, out var content) ||
                content.ValueKind != JsonValueKind.Object)
            {
                units.Clear();
                return false;
            }

            schemaKeys.Add(container.Key);
            var groupTitle = FirstSchemaNodeTitle(container);
            var hebrewGroupTitle = container.HeTitle;
            schema.AltStructures.TryGetValue("Topic", out var topics);
            var introduction = container.Children.FirstOrDefault(IsIntroductionLeaf);
            if (introduction is not null &&
                content.TryGetProperty(introduction.Key, out var introductionText) &&
                HasTextContent(introductionText))
            {
                foreach (var unit in EnumerateTextUnits(
                             introductionText,
                             new List<string> { container.Key, introduction.Key },
                             cancellationToken))
                {
                    units.Add(unit with
                    {
                        ChapterTitle = groupTitle,
                        HebrewChapterTitle = hebrewGroupTitle,
                        NavigationKey = $"{container.Key}.{introduction.Key}",
                        NavigationLabel = FirstSchemaNodeTitle(introduction),
                        HebrewNavigationLabel = introduction.HeTitle
                    });
                }
            }

            if (!content.TryGetProperty(body.Key, out var bodyText) || !HasTextContent(bodyText))
            {
                continue;
            }

            foreach (var unit in EnumerateTextUnits(
                         bodyText,
                         new List<string> { container.Key, body.Key },
                         cancellationToken))
            {
                var parts = unit.Reference.Split(
                    '.',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 4)
                {
                    units.Clear();
                    return false;
                }

                var siman = parts[2];
                var (topicTitle, hebrewTopicTitle) = topics is { Count: > 0 }
                    ? GetTopicTitleFromSchema(topics, siman, container.Key)
                    : (string.Empty, string.Empty);
                units.Add(unit with
                {
                    ChapterTitle = string.IsNullOrWhiteSpace(topicTitle)
                        ? groupTitle
                        : $"{groupTitle} — {topicTitle}",
                    HebrewChapterTitle = string.IsNullOrWhiteSpace(hebrewTopicTitle)
                        ? hebrewGroupTitle
                        : $"{hebrewGroupTitle} — {hebrewTopicTitle}",
                    NavigationKey = $"{container.Key}.{siman}",
                    NavigationLabel = siman
                });
            }
        }

        if (rawText.EnumerateObject().Any(property =>
                HasTextContent(property.Value) && !schemaKeys.Contains(property.Name)))
        {
            units.Clear();
            return false;
        }

        return units.Count > 0;
    }

    private static bool TryMatchNamedChapterVerseProfile(
        SefariaSchemaNode schemaRoot,
        JsonElement rawText,
        out List<(SefariaSchemaNode Container, SefariaSchemaNode Introduction, SefariaSchemaNode Body, JsonElement Content)> containers)
    {
        containers = new();
        var schemaKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in schemaRoot.Children)
        {
            if (container.IsDefault || string.IsNullOrWhiteSpace(container.Key) || container.Children.Count != 2)
            {
                return false;
            }

            var introductions = container.Children.Where(IsIntroductionLeaf).Take(2).ToList();
            var bodies = container.Children.Where(IsChapterVerseParagraphLeaf).Take(2).ToList();
            if (introductions.Count != 1 || bodies.Count != 1 ||
                !rawText.TryGetProperty(container.Key, out var content) ||
                content.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var introduction = introductions[0];
            var body = bodies[0];

            var childKeys = new HashSet<string>(
                new[] { introduction.Key, body.Key },
                StringComparer.Ordinal);
            if (content.EnumerateObject().Any(property =>
                    HasTextContent(property.Value) && !childKeys.Contains(property.Name)))
            {
                return false;
            }

            schemaKeys.Add(container.Key);
            containers.Add((container, introduction, body, content));
        }

        if (rawText.EnumerateObject().Any(property =>
                HasTextContent(property.Value) && !schemaKeys.Contains(property.Name)))
        {
            containers.Clear();
            return false;
        }

        return containers.Count > 0;
    }

    private static bool IsIntroductionLeaf(SefariaSchemaNode node) =>
        !node.IsDefault &&
        node.Children.Count == 0 &&
        node.Depth == 1 &&
        node.SectionNames.Count == 1 &&
        string.Equals(node.SectionNames[0], "Paragraph", StringComparison.OrdinalIgnoreCase) &&
        (node.Key.Contains("Introduction", StringComparison.OrdinalIgnoreCase) ||
         node.Title.Contains("Introduction", StringComparison.OrdinalIgnoreCase) ||
         node.SharedTitle.Contains("Introduction", StringComparison.OrdinalIgnoreCase));

    private static bool IsChapterVerseParagraphLeaf(SefariaSchemaNode node) =>
        node.IsDefault &&
        node.Children.Count == 0 &&
        node.Depth == 3 &&
        node.SectionNames.Count == 3 &&
        string.Equals(node.SectionNames[0], "Chapter", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(node.SectionNames[1], "Verse", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(node.SectionNames[2], "Paragraph", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(node.SectionNames[2], "Comment", StringComparison.OrdinalIgnoreCase)) &&
        (node.AddressTypes.Count == 0 ||
         !node.AddressTypes.Any(addressType =>
             string.Equals(addressType, "Talmud", StringComparison.OrdinalIgnoreCase)));

    private static string FirstSchemaNodeTitle(SefariaSchemaNode node) =>
        !string.IsNullOrWhiteSpace(node.Title)
            ? node.Title
            : !string.IsNullOrWhiteSpace(node.SharedTitle)
                ? node.SharedTitle
                : node.Key;

    private static IEnumerable<ReaderTextUnit> EnumerateTextUnits(
        JsonElement element,
        List<string> path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new ReaderTextUnit(string.Join(".", path), text);
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextPath = new List<string>(path) { property.Name };
                foreach (var unit in EnumerateTextUnits(property.Value, nextPath, cancellationToken))
                {
                    yield return unit;
                }
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextPath = new List<string>(path) { index.ToString() };
            foreach (var unit in EnumerateTextUnits(item, nextPath, cancellationToken))
            {
                yield return unit;
            }

            index++;
        }
    }

    private static void AppendMishnahTextUnits(
        JsonElement textElement,
        List<ReaderTextUnit> units,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (textElement.ValueKind != JsonValueKind.Array)
        {
            AppendTextUnits(textElement, units, new List<string>(), cancellationToken);
            return;
        }

        var chapterNumber = 1;
        foreach (var chapter in textElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chapter.ValueKind != JsonValueKind.Array)
            {
                var chapterText = NormalizeMishnahUnitText(CollectText(chapter, cancellationToken));
                if (!string.IsNullOrWhiteSpace(chapterText))
                {
                    units.Add(new ReaderTextUnit(chapterNumber.ToString(), chapterText));
                }

                chapterNumber++;
                continue;
            }

            var mishnahNumber = 1;
            foreach (var mishnah in chapter.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mishnahText = NormalizeMishnahUnitText(CollectText(mishnah, cancellationToken));
                if (!string.IsNullOrWhiteSpace(mishnahText))
                {
                    units.Add(new ReaderTextUnit($"{chapterNumber}.{mishnahNumber}", mishnahText));
                }

                mishnahNumber++;
            }

            chapterNumber++;
        }
    }

    private static IEnumerable<ReaderTextUnit> EnumerateMishnahTextUnits(
        JsonElement textElement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (textElement.ValueKind != JsonValueKind.Array)
        {
            foreach (var unit in EnumerateTextUnits(textElement, new List<string>(), cancellationToken))
            {
                yield return unit;
            }

            yield break;
        }

        var chapterNumber = 1;
        foreach (var chapter in textElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chapter.ValueKind != JsonValueKind.Array)
            {
                var chapterText = NormalizeMishnahUnitText(CollectText(chapter, cancellationToken));
                if (!string.IsNullOrWhiteSpace(chapterText))
                {
                    yield return new ReaderTextUnit(chapterNumber.ToString(), chapterText);
                }

                chapterNumber++;
                continue;
            }

            var mishnahNumber = 1;
            foreach (var mishnah in chapter.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mishnahText = NormalizeMishnahUnitText(CollectText(mishnah, cancellationToken));
                if (!string.IsNullOrWhiteSpace(mishnahText))
                {
                    yield return new ReaderTextUnit($"{chapterNumber}.{mishnahNumber}", mishnahText);
                }

                mishnahNumber++;
            }

            chapterNumber++;
        }
    }

    private static string CollectText(JsonElement element)
    {
        return CollectText(element, CancellationToken.None);
    }

    private static string CollectText(JsonElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = CollectText(item, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(" ", parts);
    }

    private static string NormalizeMishnahUnitText(string text)
    {
        return CollapseWhitespace(RemoveSmallTagsWithContent(text));
    }

    private static string RemoveSmallTagsWithContent(string text)
    {
        var builder = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var smallStart = text.IndexOf("<small", position, StringComparison.OrdinalIgnoreCase);
            if (smallStart < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, smallStart - position);
            var smallEnd = text.IndexOf("</small>", smallStart, StringComparison.OrdinalIgnoreCase);
            if (smallEnd < 0)
            {
                var openingEnd = text.IndexOf('>', smallStart);
                position = openingEnd < 0 ? text.Length : openingEnd + 1;
            }
            else
            {
                position = smallEnd + "</small>".Length;
            }
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static string ReadJsonTextFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static List<ReaderTextUnit> ReadTalmudTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        BookSchema? schema = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var units = new List<ReaderTextUnit>();
        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var chapterTitle = string.Empty;
            var hebrewChapterTitle = string.Empty;
            foreach (var pageRoot in pages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
                if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                    !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
                {
                    chapterTitle = pageChapterTitles.ChapterTitle;
                    hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
                }

                AppendTalmudPageTextUnits(book, pageRoot, units, chapterTitle, hebrewChapterTitle, cancellationToken);
            }

            return units;
        }

        if (root.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.Array &&
            textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            AppendDirectTalmudTextUnits(textElement, units, schema, book, cancellationToken);
            return units;
        }

        var (singlePageChapterTitle, singlePageHebrewChapterTitle) = GetTalmudChapterTitles(root);
        AppendTalmudPageTextUnits(book, root, units, singlePageChapterTitle, singlePageHebrewChapterTitle, cancellationToken);
        return units;
    }

    private static IEnumerable<ReaderTextUnit> EnumerateTalmudTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        BookSchema? schema,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var chapterTitle = string.Empty;
            var hebrewChapterTitle = string.Empty;
            foreach (var pageRoot in pages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
                if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                    !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
                {
                    chapterTitle = pageChapterTitles.ChapterTitle;
                    hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
                }

                foreach (var unit in EnumerateTalmudPageTextUnits(
                    book,
                    pageRoot,
                    chapterTitle,
                    hebrewChapterTitle,
                    cancellationToken))
                {
                    yield return unit;
                }
            }

            yield break;
        }

        if (root.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.Array &&
            textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            foreach (var unit in EnumerateDirectTalmudTextUnits(textElement, schema, book, cancellationToken))
            {
                yield return unit;
            }

            yield break;
        }

        var (singlePageChapterTitle, singlePageHebrewChapterTitle) = GetTalmudChapterTitles(root);
        foreach (var unit in EnumerateTalmudPageTextUnits(
            book,
            root,
            singlePageChapterTitle,
            singlePageHebrewChapterTitle,
            cancellationToken))
        {
            yield return unit;
        }
    }

    private static void AppendDirectTalmudTextUnits(
        JsonElement textElement,
        List<ReaderTextUnit> units,
        BookSchema? schema,
        InstalledSefariaBook book,
        CancellationToken cancellationToken = default)
    {
        var address = 0;
        foreach (var pageElement in textElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = FormatTalmudPageFromAddress(address);
            var (chTitle, chHe) = GetChapterTitleFromSchema(schema, page);
            AppendTalmudDafNestedUnits(pageElement, units, page, chTitle, chHe, cancellationToken);
            address++;
        }
    }

    private static IEnumerable<ReaderTextUnit> EnumerateDirectTalmudTextUnits(
        JsonElement textElement,
        BookSchema? schema,
        InstalledSefariaBook book,
        CancellationToken cancellationToken)
    {
        var address = 0;
        foreach (var pageElement in textElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = FormatTalmudPageFromAddress(address);
            var (chTitle, chHe) = GetChapterTitleFromSchema(schema, page);
            foreach (var unit in EnumerateTalmudDafNestedUnits(pageElement, page, chTitle, chHe, cancellationToken))
            {
                yield return unit;
            }

            address++;
        }
    }

    /// <summary>
    /// Emits daf-relative units preserving Sefaria indices (including empty slots for numbering).
    /// Depth 2: <c>2a.12</c>. Depth 3 (Rashi line/comment): <c>2a.12.1</c>.
    /// </summary>
    private static void AppendTalmudDafNestedUnits(
        JsonElement pageElement,
        List<ReaderTextUnit> units,
        string page,
        string chapterTitle,
        string hebrewChapterTitle,
        CancellationToken cancellationToken)
    {
        foreach (var unit in EnumerateTalmudDafNestedUnits(
                     pageElement, page, chapterTitle, hebrewChapterTitle, cancellationToken))
        {
            units.Add(unit);
        }
    }

    private static IEnumerable<ReaderTextUnit> EnumerateTalmudDafNestedUnits(
        JsonElement pageElement,
        string page,
        string chapterTitle,
        string hebrewChapterTitle,
        CancellationToken cancellationToken)
    {
        if (pageElement.ValueKind != JsonValueKind.Array)
        {
            var text = CollapseWhitespace(CollectText(pageElement, cancellationToken));
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new ReaderTextUnit($"{page}.1", text, chapterTitle, hebrewChapterTitle);
            }

            yield break;
        }

        var lineIndex = 0;
        foreach (var lineElement in pageElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineIndex++;

            if (lineElement.ValueKind == JsonValueKind.Array &&
                lineElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array ||
                                                         item.ValueKind == JsonValueKind.String))
            {
                // Depth 3+: line → comments (keep 1-based line/comment indices even when empty).
                var hasNestedArrays = lineElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array);
                if (hasNestedArrays)
                {
                    // Unexpected deeper nesting: collapse line.
                    var collapsed = CollapseWhitespace(CollectText(lineElement, cancellationToken));
                    if (!string.IsNullOrWhiteSpace(collapsed))
                    {
                        yield return new ReaderTextUnit(
                            $"{page}.{lineIndex}",
                            collapsed,
                            chapterTitle,
                            hebrewChapterTitle);
                    }

                    continue;
                }

                var commentIndex = 0;
                foreach (var commentElement in lineElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    commentIndex++;
                    var commentText = CollapseWhitespace(CollectText(commentElement, cancellationToken));
                    if (string.IsNullOrWhiteSpace(commentText))
                    {
                        continue;
                    }

                    yield return new ReaderTextUnit(
                        $"{page}.{lineIndex}.{commentIndex}",
                        commentText,
                        chapterTitle,
                        hebrewChapterTitle);
                }

                continue;
            }

            var lineText = CollapseWhitespace(CollectText(lineElement, cancellationToken));
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                yield return new ReaderTextUnit(
                    $"{page}.{lineIndex}",
                    lineText,
                    chapterTitle,
                    hebrewChapterTitle);
            }
        }
    }

    private static string FormatTalmudPageFromAddress(int address)
    {
        var daf = address / 2 + 1;
        var side = address % 2 == 0 ? "a" : "b";
        return $"{daf}{side}";
    }

    private static void AppendTalmudPageTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        List<ReaderTextUnit> units,
        string chapterTitle,
        string hebrewChapterTitle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = GetTalmudPage(root);
        var textPropertyName = IsHebrew(book) && root.TryGetProperty("he", out var heElement) && heElement.ValueKind == JsonValueKind.Array
            ? "he"
            : "text";

        if (!root.TryGetProperty(textPropertyName, out var textElement))
        {
            return;
        }

        if (textElement.ValueKind == JsonValueKind.Array)
        {
            var paragraphNumber = 1;
            foreach (var paragraph in textElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var paragraphText = CollapseWhitespace(CollectText(paragraph, cancellationToken));
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText, chapterTitle, hebrewChapterTitle));
                    paragraphNumber++;
                }
            }
        }
        else
        {
            var text = CollapseWhitespace(CollectText(textElement, cancellationToken));
            if (!string.IsNullOrWhiteSpace(text))
            {
                units.Add(new ReaderTextUnit($"{page}.1", text, chapterTitle, hebrewChapterTitle));
            }
        }
    }

    private static IEnumerable<ReaderTextUnit> EnumerateTalmudPageTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        string chapterTitle,
        string hebrewChapterTitle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = GetTalmudPage(root);
        var textPropertyName = IsHebrew(book) && root.TryGetProperty("he", out var heElement) && heElement.ValueKind == JsonValueKind.Array
            ? "he"
            : "text";

        if (!root.TryGetProperty(textPropertyName, out var textElement))
        {
            yield break;
        }

        if (textElement.ValueKind == JsonValueKind.Array)
        {
            var paragraphNumber = 1;
            foreach (var paragraph in textElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var paragraphText = CollapseWhitespace(CollectText(paragraph, cancellationToken));
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    yield return new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText, chapterTitle, hebrewChapterTitle);
                    paragraphNumber++;
                }
            }
        }
        else
        {
            var text = CollapseWhitespace(CollectText(textElement, cancellationToken));
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new ReaderTextUnit($"{page}.1", text, chapterTitle, hebrewChapterTitle);
            }
        }
    }

    private static (string ChapterTitle, string HebrewChapterTitle) GetTalmudChapterTitles(JsonElement root)
    {
        if (!root.TryGetProperty("alts", out var alts) ||
            alts.ValueKind != JsonValueKind.Array ||
            alts.GetArrayLength() == 0)
        {
            return (string.Empty, string.Empty);
        }

        foreach (var alt in alts.EnumerateArray())
        {
            if (alt.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var chapterTitle = GetFirstAltTitle(alt, "en");
            var hebrewChapterTitle = GetFirstAltTitle(alt, "he");
            if (!string.IsNullOrWhiteSpace(chapterTitle) || !string.IsNullOrWhiteSpace(hebrewChapterTitle))
            {
                return (chapterTitle, hebrewChapterTitle);
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string GetFirstAltTitle(JsonElement alt, string propertyName)
    {
        if (!alt.TryGetProperty(propertyName, out var titles) ||
            titles.ValueKind != JsonValueKind.Array ||
            titles.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return titles[0].GetString() ?? string.Empty;
    }

    private static string GetTalmudPage(JsonElement root)
    {
        if (root.TryGetProperty("sections", out var sections) &&
            sections.ValueKind == JsonValueKind.Array &&
            sections.GetArrayLength() > 0)
        {
            var page = GetJsonScalarText(sections[0]);
            if (!string.IsNullOrWhiteSpace(page))
            {
                return page;
            }
        }

        if (root.TryGetProperty("sectionRef", out var sectionRef))
        {
            var reference = sectionRef.GetString();
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length == 0 ? "1" : parts[^1];
            }
        }

        return "1";
    }

    private static string? GetJsonScalarText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetPrimaryTextElement(JsonElement root, out JsonElement textElement)
    {
        textElement = default;
        if (!root.TryGetProperty("text", out var rawTextElement))
        {
            return false;
        }

        if (rawTextElement.ValueKind == JsonValueKind.Array)
        {
            textElement = rawTextElement;
            return true;
        }

        if (rawTextElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (rawTextElement.TryGetProperty(string.Empty, out var defaultTextElement) &&
            HasTextContent(defaultTextElement))
        {
            textElement = defaultTextElement;
            return true;
        }

        if (rawTextElement.TryGetProperty("default", out defaultTextElement) &&
            HasTextContent(defaultTextElement))
        {
            textElement = defaultTextElement;
            return true;
        }

        if (HasTextContent(rawTextElement))
        {
            textElement = rawTextElement;
            return true;
        }

        return false;
    }

    private static bool HasTextContent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.EnumerateArray().Any(HasTextContent),
            JsonValueKind.Object => element.EnumerateObject().Any(property => HasTextContent(property.Value)),
            _ => false
        };
    }
}
