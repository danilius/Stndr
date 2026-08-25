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

        var relative = fullReference.StartsWith(title, StringComparison.OrdinalIgnoreCase)
            ? fullReference[title.Length..].TrimStart(' ', ',')
            : fullReference;
        if (string.IsNullOrWhiteSpace(relative)) return "";

        var preferHebrew = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var books = GetOfflineLibraryBooks()
            .Where(item => string.Equals(item.Title, title, StringComparison.Ordinal))
            .OrderBy(item => string.Equals(item.LanguageCode, language, StringComparison.OrdinalIgnoreCase)
                ? 0
                : IsHebrew(item) == preferHebrew ? 1 : 2)
            .ThenByDescending(item => item.SegmentCount);

        // Commentary editions are often complementary rather than complete. Try each suitable
        // version until one actually contains this reference instead of trusting one edition.
        foreach (var book in books)
        {
            token.ThrowIfCancellationRequested();
            var cacheKey = $"{title}|{language}|{book.OfflineVersionId}";
            if (!cache.TryGetValue(cacheKey, out var units))
            {
                units = ReadInstalledBookUnits(book, token);
                cache[cacheKey] = units;
            }

            if (units is null || units.Count == 0) continue;
            var match = FindUnitForSefariaRelative(units, relative);
            if (!string.IsNullOrWhiteSpace(match?.Text))
            {
                return match.Text;
            }
        }

        return "";
    }

    /// <summary>
    /// Matches a Sefaria relative ref (e.g. <c>Part 1 1:1</c> or <c>Introduction, Introduction 1</c>)
    /// against internal unit paths (e.g. <c>Part 1.default.1.1</c> or <c>Introduction.Introduction.1</c>).
    /// </summary>
    private static ReaderTextUnit? FindUnitForSefariaRelative(
        IReadOnlyList<ReaderTextUnit> units,
        string sefariaRelative)
    {
        var target = TokenizeSefariaRelativeReference(sefariaRelative);
        if (target.Count == 0) return null;

        ReaderTextUnit? best = null;
        var bestScore = -1;
        foreach (var unit in units)
        {
            var candidate = TokenizeUnitPathReference(unit.Reference);
            if (candidate.Count == 0) continue;

            if (TokensEqual(candidate, target))
            {
                return unit;
            }

            // Prefer the longest shared prefix (handles slightly coarser/finer addresses).
            var shared = 0;
            var n = Math.Min(candidate.Count, target.Count);
            while (shared < n &&
                   string.Equals(candidate[shared], target[shared], StringComparison.OrdinalIgnoreCase))
            {
                shared++;
            }

            if (shared == target.Count || shared == candidate.Count)
            {
                if (shared > bestScore)
                {
                    bestScore = shared;
                    best = unit;
                }
            }
        }

        return best;
    }

    /// <summary>Unit paths use dots: <c>Part 1.default.1.1</c> → [Part 1, 1, 1].</summary>
    private static List<string> TokenizeUnitPathReference(string unitReference)
    {
        if (string.IsNullOrWhiteSpace(unitReference)) return new List<string>();

        return unitReference
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part =>
                !string.Equals(part, "default", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "nodes", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Sefaria relatives: <c>Part 1 1:1</c>, <c>2a:12:1</c>, <c>Introduction, Introduction 1</c>, <c>1:1</c>.
    /// </summary>
    private static List<string> TokenizeSefariaRelativeReference(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return new List<string>();

        var tokens = new List<string>();
        foreach (var segment in relative.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Pure numeric address: 1:1:1
            if (System.Text.RegularExpressions.Regex.IsMatch(segment, @"^\d+(?::\d+)*$"))
            {
                tokens.AddRange(segment.Split(':', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            // Talmud daf address: 2a, 2a:12, 2a:12:1
            var dafAddress = System.Text.RegularExpressions.Regex.Match(
                segment,
                @"^(?<daf>\d+[ab])(?::(?<rest>\d+(?::\d+)*))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dafAddress.Success)
            {
                tokens.Add(dafAddress.Groups["daf"].Value);
                if (dafAddress.Groups["rest"].Success)
                {
                    tokens.AddRange(dafAddress.Groups["rest"].Value.Split(':', StringSplitOptions.RemoveEmptyEntries));
                }

                continue;
            }

            // Named node + trailing numeric address: "Part 1 1:1"
            var withAddress = System.Text.RegularExpressions.Regex.Match(
                segment,
                @"^(?<name>.+?)\s+(?<addr>\d+(?::\d+)*)$");
            if (withAddress.Success)
            {
                tokens.Add(withAddress.Groups["name"].Value.Trim());
                tokens.AddRange(withAddress.Groups["addr"].Value.Split(':', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }

            tokens.Add(segment);
        }

        return tokens;
    }

    private static bool TokensEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when this link is a commentary <em>on the anchor</em>, not merely a link to some
    /// commentary work. Many dump links point from e.g. Mishneh Torah to Beit Yosef on
    /// Shulchan Arukh (related/parallel law). Those works have dependence=Commentary, but they
    /// are not commentaries on the open book and must not fill the Commentaries panel.
    /// </summary>
    private static bool IsCommentaryRow(OfflineLinkRow row)
    {
        // Prefer explicit link type from the dump. Dependence alone is too broad: it marks the
        // target as "a commentary work" regardless of which base text it comments on.
        if (!string.Equals(row.LinkType, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(row.Dependence, "Commentary", StringComparison.OrdinalIgnoreCase))
        {
            // Some commentary links still use type=commentary with empty dependence; keep them.
            // If dependence is set to something else, treat as non-commentary.
            return string.IsNullOrWhiteSpace(row.Dependence);
        }

        return true;
    }

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
