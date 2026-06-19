using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteDatabase = LiteDB.LiteDatabase;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public async Task<List<SefariaLinkItem>> GetLinksAsync(
        string anchorRef,
        CancellationToken cancellationToken)
    {
        anchorRef = NormalizeLinkAnchorRef(anchorRef);
        if (string.IsNullOrWhiteSpace(anchorRef))
        {
            return new List<SefariaLinkItem>();
        }

        await LinksCacheGate.WaitAsync(cancellationToken);
        try
        {
            var cached = ReadCachedLinks(anchorRef);
            if (cached is not null)
            {
                return cached.Items;
            }
        }
        finally
        {
            LinksCacheGate.Release();
        }

        var items = await DownloadLinksAsync(anchorRef, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await LinksCacheGate.WaitAsync(cancellationToken);
        try
        {
            SaveCachedLinks(anchorRef, items);
        }
        finally
        {
            LinksCacheGate.Release();
        }

        return items;
    }

    private CachedSefariaLinkSet? ReadCachedLinks(string anchorRef)
    {
        using var db = new LiteDatabase(LinksDbPath);
        var collection = db.GetCollection<CachedSefariaLinkSet>("links");
        collection.EnsureIndex(item => item.AnchorRef, unique: true);
        return collection.FindOne(item => item.AnchorRef == anchorRef);
    }

    private void SaveCachedLinks(string anchorRef, List<SefariaLinkItem> items)
    {
        using var db = new LiteDatabase(LinksDbPath);
        var collection = db.GetCollection<CachedSefariaLinkSet>("links");
        collection.EnsureIndex(item => item.AnchorRef, unique: true);
        collection.Upsert(new CachedSefariaLinkSet
        {
            Id = anchorRef,
            AnchorRef = anchorRef,
            RetrievedAtUtc = DateTime.UtcNow,
            Items = items
        });
    }

    private static async Task<List<SefariaLinkItem>> DownloadLinksAsync(
        string anchorRef,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.sefaria.org/api/links/{Uri.EscapeDataString(anchorRef)}?with_text=0";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<SefariaLinkItem>();
        }

        var items = new List<SefariaLinkItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var category = NormalizeLinkCategory(GetJsonString(element, "category"));
            if (string.Equals(category, "Commentary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new SefariaLinkItem
            {
                Ref = GetJsonString(element, "ref"),
                AnchorRef = GetJsonString(element, "anchorRef"),
                SourceRef = FirstNonEmptyLinkValue(
                    GetJsonString(element, "sourceRef"),
                    GetJsonString(element, "ref")),
                SourceHeRef = GetJsonString(element, "sourceHeRef"),
                IndexTitle = GetJsonString(element, "index_title"),
                CollectiveTitleEnglish = GetCollectiveTitle(element, "en"),
                CollectiveTitleHebrew = GetCollectiveTitle(element, "he"),
                Category = category,
                Type = NormalizeLinkType(GetJsonString(element, "type")),
                SourceHasEnglish = GetJsonBoolean(element, "sourceHasEn"),
                AnchorVerse = GetJsonInt32(element, "anchorVerse")
            });
        }

        return items
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceRef, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeLinkAnchorRef(string anchorRef)
    {
        return string.Join(' ', anchorRef.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeLinkCategory(string category)
    {
        return string.IsNullOrWhiteSpace(category) ? "Other" : category;
    }

    private static string NormalizeLinkType(string type)
    {
        return string.IsNullOrWhiteSpace(type) ? "Other" : type;
    }

    private static bool GetJsonBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.GetBoolean();
    }

    private static int GetJsonInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result)
                ? result
                : 0;
    }

    private static string FirstNonEmptyLinkValue(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
