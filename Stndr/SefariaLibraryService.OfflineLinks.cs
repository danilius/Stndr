using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private sealed record OfflineLinkRow(
        long LinkId,
        string TargetReference,
        string TargetTitle,
        string TargetHebrewTitle,
        string CategoriesJson,
        string Dependence,
        string CollectiveTitle,
        string LinkType,
        bool HasEnglish);

    private Task<List<SefariaLinkItem>> GetOfflineLinksAsync(string anchorRef, CancellationToken token) =>
        Task.Run(() =>
        {
            var rows = QueryOfflineLinkRows(anchorRef, token);
            return rows.Where(row => !IsCommentaryRow(row))
                .Select(row => new SefariaLinkItem
                {
                    Ref = row.TargetReference,
                    AnchorRef = anchorRef,
                    SourceRef = row.TargetReference,
                    IndexTitle = row.TargetTitle,
                    CollectiveTitleEnglish = row.CollectiveTitle,
                    CollectiveTitleHebrew = row.TargetHebrewTitle,
                    Category = GetOfflineLinkCategory(row),
                    Type = string.IsNullOrWhiteSpace(row.LinkType) ? "Other" : row.LinkType,
                    SourceHasEnglish = row.HasEnglish,
                    AnchorVerse = ParseAnchorVerse(anchorRef)
                })
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceRef, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, token);

    private Task<List<SefariaCommentaryItem>> GetOfflineCommentariesAsync(string anchorRef, CancellationToken token) =>
        Task.Run(() =>
        {
            var rows = QueryOfflineLinkRows(anchorRef, token).Where(IsCommentaryRow).ToList();
            var versionCache = new Dictionary<string, List<ReaderTextUnit>?>(StringComparer.Ordinal);
            return rows.Select(row =>
            {
                token.ThrowIfCancellationRequested();
                return new SefariaCommentaryItem
                {
                    Ref = row.TargetReference,
                    AnchorRef = anchorRef,
                    IndexTitle = row.TargetTitle,
                    CollectiveTitleEnglish = row.CollectiveTitle,
                    CollectiveTitleHebrew = row.TargetHebrewTitle,
                    Category = "Commentary",
                    Type = row.LinkType,
                    Text = ReadOfflineReferenceExcerpt(row.TargetTitle, row.TargetReference, "en", versionCache, token),
                    HebrewText = ReadOfflineReferenceExcerpt(row.TargetTitle, row.TargetReference, "he", versionCache, token)
                };
            }).ToList();
        }, token);

    private List<OfflineLinkRow> QueryOfflineLinkRows(string anchorRef, CancellationToken token)
    {
        using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.id,ep.side,r0.reference,r1.reference,
                   COALESCE(w0.title,''),COALESCE(w1.title,''),
                   COALESCE(w0.he_title,''),COALESCE(w1.he_title,''),
                   COALESCE(w0.categories_json,'[]'),COALESCE(w1.categories_json,'[]'),
                   COALESCE(w0.dependence,''),COALESCE(w1.dependence,''),
                   COALESCE(w0.collective_title,''),COALESCE(w1.collective_title,''),
                   l.link_type,l.available0,l.available1
            FROM refs anchor
            JOIN link_endpoints ep ON ep.ref_id=anchor.id
            JOIN links l ON l.id=ep.link_id
            JOIN refs r0 ON r0.id=l.ref0_id
            JOIN refs r1 ON r1.id=l.ref1_id
            LEFT JOIN works w0 ON w0.id=l.work0_id
            LEFT JOIN works w1 ON w1.id=l.work1_id
            WHERE anchor.reference=$anchor
            ORDER BY l.id,ep.side
            """;
        command.Parameters.AddWithValue("$anchor", anchorRef);
        var rows = new List<OfflineLinkRow>();
        var seen = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var linkId = reader.GetInt64(0);
            if (!seen.Add(linkId)) continue;
            var matchedSide = reader.GetInt32(1);
            var targetSide = matchedSide == 0 ? 1 : 0;
            rows.Add(new OfflineLinkRow(
                linkId,
                reader.GetString(targetSide == 0 ? 2 : 3),
                reader.GetString(targetSide == 0 ? 4 : 5),
                reader.GetString(targetSide == 0 ? 6 : 7),
                reader.GetString(targetSide == 0 ? 8 : 9),
                reader.GetString(targetSide == 0 ? 10 : 11),
                reader.GetString(targetSide == 0 ? 12 : 13),
                reader.GetString(14),
                (reader.GetInt32(targetSide == 0 ? 15 : 16) & 1) != 0));
        }
        return rows;
    }

    private string ReadOfflineReferenceExcerpt(string title, string fullReference, string language,
        Dictionary<string, List<ReaderTextUnit>?> cache, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var cacheKey = $"{title}|{language}";
        if (!cache.TryGetValue(cacheKey, out var units))
        {
            var book = GetOfflineLibraryBooks().FirstOrDefault(item =>
                string.Equals(item.Title, title, StringComparison.Ordinal) &&
                string.Equals(item.LanguageCode, language, StringComparison.OrdinalIgnoreCase));
            units = book is null ? null : ReadInstalledBookUnits(book, token);
            cache[cacheKey] = units;
        }
        if (units is null) return "";
        var relative = fullReference.StartsWith(title, StringComparison.Ordinal)
            ? fullReference[title.Length..].TrimStart(' ', ',')
            : fullReference;
        relative = relative.Replace(':', '.');
        var unit = units.FirstOrDefault(item => string.Equals(item.Reference, relative, StringComparison.OrdinalIgnoreCase));
        return unit?.Text ?? "";
    }

    private static bool IsCommentaryRow(OfflineLinkRow row) =>
        string.Equals(row.Dependence, "Commentary", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(row.LinkType, "commentary", StringComparison.OrdinalIgnoreCase);

    private static string GetOfflineLinkCategory(OfflineLinkRow row)
    {
        if (IsCommentaryRow(row)) return "Commentary";
        try
        {
            var categories = JsonSerializer.Deserialize<List<string>>(row.CategoriesJson);
            return categories?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Other";
        }
        catch (JsonException) { return "Other"; }
    }

    private static int ParseAnchorVerse(string reference)
    {
        var colon = reference.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(reference[(colon + 1)..], out var verse)) return verse;
        return 0;
    }
}
