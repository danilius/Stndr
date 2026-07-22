using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    public string ContentText { get; init; } = string.Empty;
    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();
}

public sealed partial class SefariaLibraryService
{
    private const string WordsApiBaseUrl = "https://www.sefaria.org/api/words/";

    public async Task<IReadOnlyList<SefariaDictionaryEntry>> LookupDictionaryEntriesAsync(
        string word,
        CancellationToken cancellationToken,
        string? lookupRef = null)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return Array.Empty<SefariaDictionaryEntry>();
        }

        var url = BuildWordsApiUrl(word.Trim(), lookupRef);
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseDictionaryEntries(document.RootElement);
    }

    private static string BuildWordsApiUrl(string word, string? lookupRef)
    {
        var url = new StringBuilder(WordsApiBaseUrl.Length + word.Length + 64);
        url.Append(WordsApiBaseUrl);
        url.Append(Uri.EscapeDataString(word));
        url.Append("?always_consonants=1");

        if (!string.IsNullOrWhiteSpace(lookupRef))
        {
            url.Append("&lookup_ref=");
            url.Append(Uri.EscapeDataString(lookupRef.Trim()));
        }

        return url.ToString();
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

            var definitions = item.TryGetProperty("content", out var contentElement)
                ? FindDefinitions(contentElement)
                : new List<string>();
            var definition = definitions.FirstOrDefault() ?? string.Empty;

            entries.Add(new SefariaDictionaryEntry
            {
                Headword = headword,
                Transliteration = GetJsonString(item, "transliteration"),
                Pronunciation = GetJsonString(item, "pronunciation"),
                LexiconName = GetJsonString(item, "parent_lexicon"),
                Definition = definition,
                ContentText = string.Join(Environment.NewLine + Environment.NewLine, definitions),
                Refs = GetStringArray(item, "refs")
            });
        }

        return entries;
    }

    private static List<string> FindDefinitions(JsonElement element)
    {
        var definitions = new List<string>();
        AppendDefinitions(element, definitions);
        return definitions;
    }

    private static void AppendDefinitions(JsonElement element, List<string> definitions)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "definition", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is { Length: > 0 } definition)
                    {
                        definitions.Add(definition);
                        continue;
                    }

                    AppendDefinitions(property.Value, definitions);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendDefinitions(item, definitions);
                }
                break;
        }
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return arrayElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

}
