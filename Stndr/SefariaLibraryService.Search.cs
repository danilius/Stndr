using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public async Task<List<SefariaSearchResult>> SearchTextsAsync(
        SefariaTextSearchRequest request,
        CancellationToken cancellationToken)
    {
        var body = new SefariaSearchWrapperRequest
        {
            Query = request.Query,
            Field = request.Field,
            Filters = request.Filters,
            Aggs = request.Aggregations,
            Size = request.Size,
            Slop = request.Slop,
            SortFields = request.SortFields,
            SortMethod = request.SortMethod,
            SourceProjection = true,
            Type = "text"
        };

        using var response = await HttpClient.PostAsJsonAsync(
            "https://www.sefaria.org/api/search-wrapper",
            body,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseSefariaSearchResults(document.RootElement);
    }

    private static List<SefariaSearchResult> ParseSefariaSearchResults(JsonElement root)
    {
        var hits = FindSefariaSearchHits(root);
        var results = new List<SefariaSearchResult>();
        foreach (var hit in hits)
        {
            var source = hit.TryGetProperty("_source", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.Object
                    ? sourceElement
                    : hit;
            var reference = FirstNonEmptySearchValue(
                GetJsonString(source, "ref"),
                GetJsonString(source, "reference"),
                GetJsonString(source, "anchorRef"),
                GetJsonString(hit, "ref"));
            var path = GetJsonString(source, "path");
            var title = FirstNonEmptySearchValue(
                GetJsonString(source, "title"),
                GetJsonString(source, "index_title"),
                GetJsonString(source, "book"),
                ExtractSearchResultTitleFromPath(path),
                ExtractSearchResultTitle(reference));
            var snippet = FirstNonEmptySearchValue(
                GetSearchHighlight(hit),
                GetJsonText(source, "content"),
                GetJsonText(source, "exact"),
                GetJsonText(source, "naive_lemmatizer"),
                GetJsonText(source, "text"));

            if (string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(snippet))
            {
                continue;
            }

            results.Add(new SefariaSearchResult
            {
                Reference = reference,
                WorkTitle = title,
                Path = path,
                Snippet = snippet,
                VersionTitle = FirstNonEmptySearchValue(
                    GetJsonString(source, "version"),
                    GetJsonString(source, "versionTitle")),
                Score = GetJsonDouble(hit, "_score")
            });
        }

        return results;
    }

    private static IEnumerable<JsonElement> FindSefariaSearchHits(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Enumerable.Empty<JsonElement>();
        }

        if (root.TryGetProperty("hits", out var hitsElement))
        {
            if (hitsElement.ValueKind == JsonValueKind.Array)
            {
                return hitsElement.EnumerateArray().ToList();
            }

            if (hitsElement.ValueKind == JsonValueKind.Object &&
                hitsElement.TryGetProperty("hits", out var nestedHits) &&
                nestedHits.ValueKind == JsonValueKind.Array)
            {
                return nestedHits.EnumerateArray().ToList();
            }
        }

        if (root.TryGetProperty("results", out var resultsElement) &&
            resultsElement.ValueKind == JsonValueKind.Array)
        {
            return resultsElement.EnumerateArray().ToList();
        }

        return Enumerable.Empty<JsonElement>();
    }

    private static string GetSearchHighlight(JsonElement hit)
    {
        if (!hit.TryGetProperty("highlight", out var highlight) ||
            highlight.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in highlight.EnumerateObject())
        {
            var text = FlattenJsonText(property.Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static double GetJsonDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var result)
                ? result
                : 0;
    }

    private static string FirstNonEmptySearchValue(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string ExtractSearchResultTitleFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;
    }

    private static string ExtractSearchResultTitle(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var endIndex = parts.Length;
        while (endIndex > 0 && parts[endIndex - 1].Any(char.IsDigit))
        {
            endIndex--;
        }

        return endIndex <= 0 ? reference.Trim() : string.Join(' ', parts, 0, endIndex);
    }

    private sealed class SefariaSearchWrapperRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("field")]
        public string Field { get; set; } = "naive_lemmatizer";

        [JsonPropertyName("filters")]
        public List<string> Filters { get; set; } = new();

        [JsonPropertyName("aggs")]
        public List<string> Aggs { get; set; } = new();

        [JsonPropertyName("size")]
        public int Size { get; set; } = 50;

        [JsonPropertyName("slop")]
        public int Slop { get; set; } = 0;

        [JsonPropertyName("sort_fields")]
        public List<string> SortFields { get; set; } = new();

        [JsonPropertyName("sort_method")]
        public string SortMethod { get; set; } = "score";

        [JsonPropertyName("source_proj")]
        public bool SourceProjection { get; set; }
    }
}

public sealed class SefariaTextSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string Field { get; set; } = "naive_lemmatizer";
    public List<string> Filters { get; set; } = new();
    public List<string> Aggregations { get; set; } = new();
    public int Size { get; set; } = 50;
    public int Slop { get; set; }
    public List<string> SortFields { get; set; } = new() { "pagesheetrank" };
    public string SortMethod { get; set; } = "score";
}

public sealed class SefariaSearchResult
{
    public string Reference { get; set; } = string.Empty;
    public string WorkTitle { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string VersionTitle { get; set; } = string.Empty;
    public double Score { get; set; }
}
