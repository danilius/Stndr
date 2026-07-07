using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed class SefariaDictionaryEntry
{
    public string Headword { get; init; } = string.Empty;
    public string Transliteration { get; init; } = string.Empty;
    public string Pronunciation { get; init; } = string.Empty;
    public string LexiconName { get; init; } = string.Empty;
    public string Definition { get; init; } = string.Empty;
}

public sealed partial class SefariaLibraryService
{
    private const string WordsApiBaseUrl = "https://www.sefaria.org/api/words/";

    public async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupDictionaryEntriesAsync(
        string word,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        var url = WordsApiBaseUrl + Uri.EscapeDataString(word.Trim());
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseDictionaryEntries(document.RootElement);
    }

    private static IReadOnlyList<SefariaDictionaryEntry> ParseDictionaryEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        var entries = new List<SefariaDictionaryEntry>();
        foreach (var item in root.EnumerateArray())
        {
            var headword = GetJsonString(item, "headword");
            if (string.IsNullOrWhiteSpace(headword))
            {
                continue;
            }

            var definition = item.TryGetProperty("content", out var contentElement)
                ? FindFirstDefinition(contentElement)
                : string.Empty;

            entries.Add(new SefariaDictionaryEntry
            {
                Headword = headword,
                Transliteration = GetJsonString(item, "transliteration"),
                Pronunciation = GetJsonString(item, "pronunciation"),
                LexiconName = GetJsonString(item, "parent_lexicon"),
                Definition = definition
            });
        }

        return entries;
    }

    private static string FindFirstDefinition(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => FindFirstDefinitionInObject(element),
            JsonValueKind.Array => FindFirstDefinitionInArray(element),
            _ => string.Empty
        };
    }

    private static string FindFirstDefinitionInObject(JsonElement element)
    {
        if (element.TryGetProperty("definition", out var definitionElement) &&
            definitionElement.ValueKind == JsonValueKind.String &&
            definitionElement.GetString() is { Length: > 0 } definition)
        {
            return definition;
        }

        foreach (var property in element.EnumerateObject())
        {
            var candidate = FindFirstDefinition(property.Value);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string FindFirstDefinitionInArray(JsonElement element)
    {
        foreach (var item in element.EnumerateArray())
        {
            var candidate = FindFirstDefinition(item);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
