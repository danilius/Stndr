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
        if (!document.RootElement.TryGetProperty("text", out var textElement))
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
            return ReadTalmudTextUnits(book, root);
        }

        if (!root.TryGetProperty("text", out var textElement))
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

        AppendTextUnits(textElement, units, new List<int>());
        return units;
    }

    public List<ReaderNavigationPage> ReadInstalledBookNavigationPages(InstalledSefariaBook book)
    {
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!IsTalmud(book) ||
            !root.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return ReadDirectTalmudNavigationPages(book, root);
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

            var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
            if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
            {
                chapterTitle = pageChapterTitles.ChapterTitle;
                hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
            }

            navigationPages.Add(new ReaderNavigationPage(page, chapterTitle, hebrewChapterTitle));
        }

        return navigationPages;
    }

    private static List<ReaderNavigationPage> ReadDirectTalmudNavigationPages(InstalledSefariaBook book, JsonElement root)
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
            navigationPages.Add(new ReaderNavigationPage(FormatTalmudPageFromAddress(address), string.Empty, string.Empty));
            address++;
        }

        return navigationPages;
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

    private static void AppendTextUnits(JsonElement element, List<ReaderTextUnit> units, List<int> path)
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

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            var nextPath = new List<int>(path) { index };
            AppendTextUnits(item, units, nextPath);
            index++;
        }
    }

    private static void AppendMishnahTextUnits(JsonElement textElement, List<ReaderTextUnit> units)
    {
        if (textElement.ValueKind != JsonValueKind.Array)
        {
            AppendTextUnits(textElement, units, new List<int>());
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

    private static List<ReaderTextUnit> ReadTalmudTextUnits(InstalledSefariaBook book, JsonElement root)
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
            AppendDirectTalmudTextUnits(textElement, units);
            return units;
        }

        var (singlePageChapterTitle, singlePageHebrewChapterTitle) = GetTalmudChapterTitles(root);
        AppendTalmudPageTextUnits(book, root, units, singlePageChapterTitle, singlePageHebrewChapterTitle);
        return units;
    }

    private static void AppendDirectTalmudTextUnits(JsonElement textElement, List<ReaderTextUnit> units)
    {
        var address = 0;
        foreach (var pageElement in textElement.EnumerateArray())
        {
            var page = FormatTalmudPageFromAddress(address);
            if (pageElement.ValueKind == JsonValueKind.Array)
            {
                var paragraphNumber = 1;
                foreach (var paragraph in pageElement.EnumerateArray())
                {
                    var paragraphText = CollapseWhitespace(CollectText(paragraph));
                    if (!string.IsNullOrWhiteSpace(paragraphText))
                    {
                        units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText));
                        paragraphNumber++;
                    }
                }
            }
            else
            {
                var text = CollapseWhitespace(CollectText(pageElement));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    units.Add(new ReaderTextUnit($"{page}.1", text));
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
}
