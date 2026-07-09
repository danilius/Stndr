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
        var json = ReadJsonTextFile(book.FilePath);
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
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (IsTalmud(book))
        {
            var schema = GetBookSchema(book.Title);
            return ReadTalmudTextUnits(book, root, schema);
        }

        if (!TryGetPrimaryTextElement(root, out var textElement))
        {
            return new List<ReaderTextUnit>
            {
                new("1", json)
            };
        }

        var units = new List<ReaderTextUnit>();
        if (IsMishnah(book))
        {
            AppendMishnahTextUnits(textElement, units);
            return units;
        }

        AppendTextUnits(textElement, units, new List<string>());
        return units;
    }

    public List<ReaderNavigationPage> ReadInstalledBookNavigationPages(InstalledSefariaBook book)
    {
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        BookSchema? schema = null;
        if (IsTalmud(book))
        {
            schema = GetBookSchema(book.Title);
        }

        if (IsShulchanArukh(book))
        {
            var shulchanPages = ReadShulchanArukhNavigationPages(book, root);
            if (shulchanPages.Count > 0)
            {
                return shulchanPages;
            }
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
        if (!IsTalmud(book) ||
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

    private static (string ChapterTitle, string HebrewChapterTitle) GetChapterTitleFromSchema(BookSchema? schema, string page)
    {
        if (schema != null && schema.AltStructures.TryGetValue("Chapters", out var chs) && chs.Count > 0)
        {
            double current = ToDafNumber(page);
            SchemaAltNode best = null;
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
        string section)
    {
        if (!int.TryParse(section, out var sectionNumber))
        {
            return (string.Empty, string.Empty);
        }

        foreach (var topic in topics)
        {
            if (TryParseWholeRefSimanRange(topic.WholeRef, out var start, out var end) &&
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

    private static bool IsTalmud(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Talmud", StringComparison.OrdinalIgnoreCase));
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

    private static void AppendTextUnits(JsonElement element, List<ReaderTextUnit> units, List<string> path)
    {
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
                var nextPath = new List<string>(path) { property.Name };
                AppendTextUnits(property.Value, units, nextPath);
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
            var nextPath = new List<string>(path) { index.ToString() };
            AppendTextUnits(item, units, nextPath);
            index++;
        }
    }

    private static void AppendMishnahTextUnits(JsonElement textElement, List<ReaderTextUnit> units)
    {
        if (textElement.ValueKind != JsonValueKind.Array)
        {
            AppendTextUnits(textElement, units, new List<string>());
            return;
        }

        var chapterNumber = 1;
        foreach (var chapter in textElement.EnumerateArray())
        {
            if (chapter.ValueKind != JsonValueKind.Array)
            {
                var chapterText = NormalizeMishnahUnitText(CollectText(chapter));
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
                var mishnahText = NormalizeMishnahUnitText(CollectText(mishnah));
                if (!string.IsNullOrWhiteSpace(mishnahText))
                {
                    units.Add(new ReaderTextUnit($"{chapterNumber}.{mishnahNumber}", mishnahText));
                }

                mishnahNumber++;
            }

            chapterNumber++;
        }
    }

    private static string CollectText(JsonElement element)
    {
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
            var text = CollectText(item);
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

    private static List<ReaderTextUnit> ReadTalmudTextUnits(InstalledSefariaBook book, JsonElement root, BookSchema? schema = null)
    {
        var units = new List<ReaderTextUnit>();
        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var chapterTitle = string.Empty;
            var hebrewChapterTitle = string.Empty;
            foreach (var pageRoot in pages.EnumerateArray())
            {
                var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
                if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                    !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
                {
                    chapterTitle = pageChapterTitles.ChapterTitle;
                    hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
                }

                AppendTalmudPageTextUnits(book, pageRoot, units, chapterTitle, hebrewChapterTitle);
            }

            return units;
        }

        if (root.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.Array &&
            textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            AppendDirectTalmudTextUnits(textElement, units, schema, book);
            return units;
        }

        var (singlePageChapterTitle, singlePageHebrewChapterTitle) = GetTalmudChapterTitles(root);
        AppendTalmudPageTextUnits(book, root, units, singlePageChapterTitle, singlePageHebrewChapterTitle);
        return units;
    }

    private static void AppendDirectTalmudTextUnits(JsonElement textElement, List<ReaderTextUnit> units, BookSchema? schema, InstalledSefariaBook book)
    {
        var address = 0;
        foreach (var pageElement in textElement.EnumerateArray())
        {
            var page = FormatTalmudPageFromAddress(address);
            var (chTitle, chHe) = GetChapterTitleFromSchema(schema, page);
            if (pageElement.ValueKind == JsonValueKind.Array)
            {
                var paragraphNumber = 1;
                foreach (var paragraph in pageElement.EnumerateArray())
                {
                    var paragraphText = CollapseWhitespace(CollectText(paragraph));
                    if (!string.IsNullOrWhiteSpace(paragraphText))
                    {
                        units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText, chTitle, chHe));
                        paragraphNumber++;
                    }
                }
            }
            else
            {
                var text = CollapseWhitespace(CollectText(pageElement));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    units.Add(new ReaderTextUnit($"{page}.1", text, chTitle, chHe));
                }
            }

            address++;
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
        string hebrewChapterTitle)
    {
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
                var paragraphText = CollapseWhitespace(CollectText(paragraph));
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText, chapterTitle, hebrewChapterTitle));
                    paragraphNumber++;
                }
            }
        }
        else
        {
            var text = CollapseWhitespace(CollectText(textElement));
            if (!string.IsNullOrWhiteSpace(text))
            {
                units.Add(new ReaderTextUnit($"{page}.1", text, chapterTitle, hebrewChapterTitle));
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

        foreach (var property in rawTextElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                textElement = property.Value;
                return true;
            }

            if (property.Value.ValueKind == JsonValueKind.Object &&
                HasTextContent(property.Value))
            {
                textElement = rawTextElement;
                return true;
            }
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
