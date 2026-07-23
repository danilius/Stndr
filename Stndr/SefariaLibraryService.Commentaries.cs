using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteDatabase = LiteDB.LiteDatabase;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public async Task<List<SefariaCommentaryItem>> GetCommentariesAsync(
        string anchorRef,
        CancellationToken cancellationToken)
    {
        anchorRef = NormalizeCommentaryAnchorRef(anchorRef);
        if (string.IsNullOrWhiteSpace(anchorRef))
        {
            return new List<SefariaCommentaryItem>();
        }

        if (HasOfflineLibrary)
        {
            return await GetOfflineCommentariesAsync(anchorRef, cancellationToken);
        }

        await CommentaryCacheGate.WaitAsync(cancellationToken);
        try
        {
            var cached = ReadCachedCommentaries(anchorRef);
            if (cached is not null)
            {
                return cached.Items;
            }
        }
        finally
        {
            CommentaryCacheGate.Release();
        }

        var items = await DownloadCommentariesAsync(anchorRef, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await CommentaryCacheGate.WaitAsync(cancellationToken);
        try
        {
            SaveCachedCommentaries(anchorRef, items);
        }
        finally
        {
            CommentaryCacheGate.Release();
        }

        return items;
    }

    private CachedSefariaCommentarySet? ReadCachedCommentaries(string anchorRef)
    {
        using var db = new LiteDatabase(CommentariesDbPath);
        var collection = db.GetCollection<CachedSefariaCommentarySet>("commentaries");
        collection.EnsureIndex(item => item.AnchorRef, unique: true);
        return collection.FindOne(item => item.AnchorRef == anchorRef);
    }

    private void SaveCachedCommentaries(string anchorRef, List<SefariaCommentaryItem> items)
    {
        using var db = new LiteDatabase(CommentariesDbPath);
        var collection = db.GetCollection<CachedSefariaCommentarySet>("commentaries");
        collection.EnsureIndex(item => item.AnchorRef, unique: true);
        collection.Upsert(new CachedSefariaCommentarySet
        {
            Id = anchorRef,
            AnchorRef = anchorRef,
            RetrievedAtUtc = DateTime.UtcNow,
            Items = items
        });
    }

    private static async Task<List<SefariaCommentaryItem>> DownloadCommentariesAsync(
        string anchorRef,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.sefaria.org/api/links/{Uri.EscapeDataString(anchorRef)}?with_text=1";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<SefariaCommentaryItem>();
        }

        var items = new List<SefariaCommentaryItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var category = GetJsonString(element, "category");
            var type = GetJsonString(element, "type");
            if (!string.Equals(category, "Commentary", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(type, "commentary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new SefariaCommentaryItem
            {
                Ref = GetJsonString(element, "ref"),
                AnchorRef = GetJsonString(element, "anchorRef"),
                IndexTitle = GetJsonString(element, "index_title"),
                CollectiveTitleEnglish = GetCollectiveTitle(element, "en"),
                CollectiveTitleHebrew = GetCollectiveTitle(element, "he"),
                Category = category,
                Type = type,
                Text = GetJsonText(element, "text"),
                HebrewText = GetJsonText(element, "he"),
                VersionTitle = GetJsonString(element, "versionTitle"),
                HebrewVersionTitle = GetJsonString(element, "heVersionTitle"),
                License = GetJsonString(element, "license"),
                HebrewLicense = GetJsonString(element, "heLicense")
            });
        }

        return items;
    }

    private static string NormalizeCommentaryAnchorRef(string anchorRef)
    {
        return string.Join(' ', anchorRef.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetCollectiveTitle(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty("collectiveTitle", out var collectiveTitle) ||
            collectiveTitle.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return GetJsonString(collectiveTitle, propertyName);
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetJsonText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return FlattenJsonText(value);
    }

    private static string FlattenJsonText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n",
                element.EnumerateArray()
                    .Select(FlattenJsonText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => string.Empty
        };
    }
}
