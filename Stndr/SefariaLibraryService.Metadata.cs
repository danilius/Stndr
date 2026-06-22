using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private sealed class SefariaExportBooksManifest
    {
        [JsonPropertyName("books")]
        public List<SefariaExportBookEntry> Books { get; set; } = new();
    }

    private sealed class SefariaExportBookEntry
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("language")]
        public string Language { get; set; } = string.Empty;

        [JsonPropertyName("versionTitle")]
        public string VersionTitle { get; set; } = string.Empty;

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new();

        [JsonPropertyName("json_url")]
        public string JsonUrl { get; set; } = string.Empty;
    }

    public async Task<List<SefariaVersionOption>> GetAvailableVersionsAsync(string title, CancellationToken cancellationToken)
    {
        var manifest = await GetBooksManifestLookupAsync(cancellationToken);
        return manifest.TryGetValue(title, out var versions)
            ? versions.Select(CloneVersionOption).ToList()
            : new List<SefariaVersionOption>();
    }

    public async Task WarmBooksManifestCacheAsync(CancellationToken cancellationToken)
    {
        _ = await GetBooksManifestLookupAsync(cancellationToken);
    }

    public async Task<Dictionary<string, List<SefariaVersionOption>>> GetAllAvailableVersionsLookupAsync(CancellationToken cancellationToken)
    {
        var manifest = await GetBooksManifestLookupAsync(cancellationToken);
        return manifest.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(CloneVersionOption).ToList(),
            StringComparer.Ordinal);
    }

    public bool TryGetCachedAvailableVersions(string title, out List<SefariaVersionOption> versions)
    {
        if (_booksManifestCache is not null &&
            _booksManifestCache.TryGetValue(title, out var cachedVersions))
        {
            versions = cachedVersions.Select(CloneVersionOption).ToList();
            return true;
        }

        versions = new List<SefariaVersionOption>();
        return false;
    }

    private async Task<Dictionary<string, List<SefariaVersionOption>>> GetBooksManifestLookupAsync(CancellationToken cancellationToken)
    {
        if (_booksManifestCache is not null)
        {
            return _booksManifestCache;
        }

        await _booksManifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_booksManifestCache is not null)
            {
                return _booksManifestCache;
            }

            var manifestJson = await EnsureBooksManifestAvailableAsync(cancellationToken);
            _booksManifestCache = await Task.Run(() =>
            {
                var manifest = JsonSerializer.Deserialize<SefariaExportBooksManifest>(manifestJson) ?? new SefariaExportBooksManifest();
                return manifest.Books
                    .Where(entry =>
                        !string.IsNullOrWhiteSpace(entry.Title) &&
                        !string.IsNullOrWhiteSpace(entry.Language) &&
                        !string.IsNullOrWhiteSpace(entry.VersionTitle) &&
                        !string.IsNullOrWhiteSpace(entry.JsonUrl))
                    .GroupBy(entry => entry.Title, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .Select(CreateVersionOption)
                            .GroupBy(version => $"{version.LanguageCode}|{version.VersionTitle}", StringComparer.OrdinalIgnoreCase)
                            .Select(versionGroup => versionGroup.First())
                            .OrderByDescending(IsHebrewLanguageCode)
                            .ThenByDescending(version => string.Equals(version.VersionTitle, "merged", StringComparison.OrdinalIgnoreCase))
                            .ThenBy(version => version.VersionTitle, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        StringComparer.Ordinal);
            }, cancellationToken);

            return _booksManifestCache;
        }
        finally
        {
            _booksManifestGate.Release();
        }
    }

    private static SefariaVersionOption CreateVersionOption(SefariaExportBookEntry entry)
    {
        var languageCode = NormalizeExportLanguageCode(entry.Language, entry.VersionTitle);
        return new SefariaVersionOption
        {
            LanguageCode = languageCode,
            LanguageFamilyName = languageCode == "he" ? "hebrew" : "english",
            VersionTitle = entry.VersionTitle,
            DisplayText = BuildExportDisplayText(entry.VersionTitle, languageCode),
            DownloadUrl = entry.JsonUrl
        };
    }

    private static SefariaVersionOption CloneVersionOption(SefariaVersionOption version)
    {
        return new SefariaVersionOption
        {
            LanguageCode = version.LanguageCode,
            LanguageFamilyName = version.LanguageFamilyName,
            VersionTitle = version.VersionTitle,
            DisplayText = version.DisplayText,
            DownloadUrl = version.DownloadUrl
        };
    }

    private static string NormalizeExportLanguageCode(string language, string versionTitle)
    {
        var code = string.Equals(language, "Hebrew", StringComparison.OrdinalIgnoreCase) ? "he" : "en";
        if (code == "he" && LooksLikeTranslationVersionTitle(versionTitle))
        {
            return "en";
        }

        return code;
    }

    private static bool IsHebrewLanguageCode(SefariaVersionOption version)
    {
        return string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExportDisplayText(string versionTitle, string languageCode)
    {
        if (string.Equals(versionTitle, "merged", StringComparison.OrdinalIgnoreCase))
        {
            return languageCode == "he" ? "Merged Hebrew" : "Merged translation";
        }

        return versionTitle;
    }

    private static bool LooksLikeTranslationVersionTitle(string? versionTitle)
    {
        if (string.IsNullOrWhiteSpace(versionTitle))
        {
            return false;
        }

        var normalized = versionTitle.ToLowerInvariant();
        if (normalized.Contains("translation") ||
            normalized.Contains("translated") ||
            normalized.Contains("trans.") ||
            normalized.Contains(" trans "))
        {
            return true;
        }

        var start = normalized.LastIndexOf("[", StringComparison.Ordinal);
        var end = normalized.LastIndexOf("]", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var code = normalized[(start + 1)..end].Trim();
            if (!string.IsNullOrWhiteSpace(code) &&
                !string.Equals(code, "he", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
