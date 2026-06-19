using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteDatabase = LiteDB.LiteDatabase;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private const string LegacyLinkPreviewFolderName = "LinkPreviewExcerpts";

    public async Task<SefariaLinkPreview?> GetLinkPreviewAsync(
        SefariaLinkItem link,
        CancellationToken cancellationToken)
    {
        var fullReference = GetLinkFullReference(link);
        var workTitle = ResolveLinkWorkTitle(link);
        if (string.IsNullOrWhiteSpace(fullReference) || string.IsNullOrWhiteSpace(workTitle))
        {
            return null;
        }

        if (TryGetInstalledLinkPreview(workTitle, fullReference, out var installedPreview))
        {
            return installedPreview;
        }

        await LinksCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCachedLinkPreview(workTitle, fullReference, out var cachedPreview))
            {
                return cachedPreview;
            }
        }
        finally
        {
            LinksCacheGate.Release();
        }

        var downloadedPreview = await DownloadLinkPreviewAsync(workTitle, fullReference, cancellationToken);
        if (downloadedPreview is null)
        {
            return null;
        }

        await LinksCacheGate.WaitAsync(cancellationToken);
        try
        {
            SaveCachedLinkPreview(downloadedPreview);
        }
        finally
        {
            LinksCacheGate.Release();
        }

        return downloadedPreview;
    }

    public async Task DownloadLinkWorkAsync(
        string workTitle,
        CommentaryLanguage preferredLanguage,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var versions = await GetAvailableVersionsAsync(workTitle, cancellationToken);
        var selectedVersion = SelectPreferredVersion(versions, preferredLanguage);
        if (selectedVersion is null)
        {
            throw new InvalidOperationException($"No downloadable versions are available for {workTitle}.");
        }

        var book = new SefariaBookNode
        {
            Title = workTitle,
            SelectedVersion = selectedVersion
        };
        try
        {
            await DownloadBookAsync(book, progress, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("did not return downloadable JSON", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Sefaria does not currently expose a full downloadable source for {workTitle}. You can still use the inline preview or split view for the linked reference.",
                ex);
        }
    }

    public List<InstalledSefariaBook> GetFullInstalledVersionsForTitle(string title)
    {
        return GetInstalledVersionsForTitle(title)
            .Where(book => !IsLinkPreviewExcerpt(book))
            .ToList();
    }

    private bool TryGetInstalledLinkPreview(
        string workTitle,
        string fullReference,
        out SefariaLinkPreview? preview)
    {
        preview = null;
        var installedVersions = GetFullInstalledVersionsForTitle(workTitle);
        if (installedVersions.Count == 0)
        {
            return false;
        }

        var referenceWithinWork = NormalizeLinkPreviewReference(ExtractReferenceWithinWork(fullReference, workTitle));
        if (string.IsNullOrWhiteSpace(referenceWithinWork))
        {
            return false;
        }

        var englishText = string.Empty;
        var hebrewText = string.Empty;
        foreach (var version in installedVersions)
        {
            if (!TryReadInstalledReferenceText(version, referenceWithinWork, out var text))
            {
                continue;
            }

            if (IsHebrew(version))
            {
                hebrewText = FirstNonEmptyPreviewValue(hebrewText, text);
            }
            else
            {
                englishText = FirstNonEmptyPreviewValue(englishText, text);
            }
        }

        if (string.IsNullOrWhiteSpace(englishText) && string.IsNullOrWhiteSpace(hebrewText))
        {
            return false;
        }

        preview = new SefariaLinkPreview
        {
            Reference = fullReference,
            WorkTitle = workTitle,
            WorkHebrewTitle = installedVersions.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.HebrewTitle))?.HebrewTitle ?? string.Empty,
            ReferenceWithinWork = referenceWithinWork,
            EnglishText = englishText,
            HebrewText = hebrewText,
            IsFromInstalledBook = true,
            Versions = installedVersions
        };
        return true;
    }

    private bool TryGetCachedLinkPreview(
        string workTitle,
        string fullReference,
        out SefariaLinkPreview? preview)
    {
        preview = null;
        CachedSefariaLinkPreview? cached;
        using (var db = new LiteDatabase(LinksDbPath))
        {
            var collection = db.GetCollection<CachedSefariaLinkPreview>("link_previews");
            collection.EnsureIndex(item => item.FullReference, unique: true);
            cached = collection.FindOne(item => item.FullReference == fullReference);
        }

        if (cached is not null)
        {
            preview = CreateLinkPreviewFromCache(cached, workTitle, fullReference);
            if (!string.IsNullOrWhiteSpace(preview.EnglishText) || !string.IsNullOrWhiteSpace(preview.HebrewText))
            {
                return true;
            }

            preview = null;
        }

        return TryImportLegacyCachedLinkPreview(workTitle, fullReference, out preview);
    }

    private bool TryImportLegacyCachedLinkPreview(
        string workTitle,
        string fullReference,
        out SefariaLinkPreview? preview)
    {
        preview = null;
        var englishFilePath = GetLegacyLinkPreviewExcerptFilePath(fullReference, "en");
        var hebrewFilePath = GetLegacyLinkPreviewExcerptFilePath(fullReference, "he");
        if (!File.Exists(englishFilePath) && !File.Exists(hebrewFilePath))
        {
            return false;
        }

        var englishText = string.Empty;
        var hebrewText = string.Empty;
        var workHebrewTitle = string.Empty;
        var englishVersionTitle = string.Empty;
        var hebrewVersionTitle = string.Empty;
        var categories = new List<string>();

        if (File.Exists(englishFilePath) &&
            TryReadLegacyCachedLinkPreviewVersion(
                englishFilePath,
                out var cachedEnglishText,
                out var englishHebrewTitle,
                out englishVersionTitle,
                out var englishCategories))
        {
            englishText = cachedEnglishText;
            workHebrewTitle = FirstNonEmptyPreviewValue(workHebrewTitle, englishHebrewTitle);
            categories = MergePreviewCategories(categories, englishCategories);
        }

        if (File.Exists(hebrewFilePath) &&
            TryReadLegacyCachedLinkPreviewVersion(
                hebrewFilePath,
                out var cachedHebrewText,
                out var hebrewWorkTitle,
                out hebrewVersionTitle,
                out var hebrewCategories))
        {
            hebrewText = cachedHebrewText;
            workHebrewTitle = FirstNonEmptyPreviewValue(workHebrewTitle, hebrewWorkTitle);
            categories = MergePreviewCategories(categories, hebrewCategories);
        }

        if (string.IsNullOrWhiteSpace(englishText) && string.IsNullOrWhiteSpace(hebrewText))
        {
            return false;
        }

        var cached = new CachedSefariaLinkPreview
        {
            Id = fullReference,
            FullReference = fullReference,
            WorkTitle = workTitle,
            WorkHebrewTitle = workHebrewTitle,
            ReferenceWithinWork = NormalizeLinkPreviewReference(ExtractReferenceWithinWork(fullReference, workTitle)),
            EnglishText = englishText,
            HebrewText = hebrewText,
            EnglishVersionTitle = englishVersionTitle,
            HebrewVersionTitle = hebrewVersionTitle,
            Categories = categories,
            RetrievedAtUtc = DateTime.UtcNow
        };
        SaveCachedLinkPreviewRecord(cached);
        preview = CreateLinkPreviewFromCache(cached, workTitle, fullReference);
        return true;
    }

    private async Task<SefariaLinkPreview?> DownloadLinkPreviewAsync(
        string workTitle,
        string fullReference,
        CancellationToken cancellationToken)
    {
        var url = $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(fullReference)}?context=0&commentary=0";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var resolvedWorkTitle = FirstNonEmptyPreviewValue(
            GetJsonString(root, "indexTitle"),
            GetJsonString(root, "book"),
            workTitle);
        var workHebrewTitle = FirstNonEmptyPreviewValue(
            GetJsonString(root, "heIndexTitle"),
            GetJsonString(root, "heBook"));
        var versionTitle = GetJsonString(root, "versionTitle");
        var hebrewVersionTitle = GetJsonString(root, "heVersionTitle");
        var referenceWithinWork = NormalizeLinkPreviewReference(ExtractReferenceWithinWork(fullReference, resolvedWorkTitle));
        var categories = root.TryGetProperty("categories", out var categoriesElement) && categoriesElement.ValueKind == JsonValueKind.Array
            ? categoriesElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList()
            : new List<string>();
        var sourceType = GetJsonString(root, "type");
        if (categories.Count == 0 && string.Equals(sourceType, "Talmud", StringComparison.OrdinalIgnoreCase))
        {
            categories.Add("Talmud");
        }
        else if (categories.Count == 0 && string.Equals(sourceType, "Mishnah", StringComparison.OrdinalIgnoreCase))
        {
            categories.Add("Mishnah");
        }

        var englishText = FlattenJsonText(root.TryGetProperty("text", out var textElement) ? textElement : default);
        var hebrewText = FlattenJsonText(root.TryGetProperty("he", out var heElement) ? heElement : default);
        if (string.IsNullOrWhiteSpace(englishText) && string.IsNullOrWhiteSpace(hebrewText))
        {
            return null;
        }

        return new SefariaLinkPreview
        {
            Reference = fullReference,
            WorkTitle = resolvedWorkTitle,
            WorkHebrewTitle = workHebrewTitle,
            ReferenceWithinWork = referenceWithinWork,
            EnglishText = englishText,
            HebrewText = hebrewText,
            IsExcerptOnly = true,
            Versions = new List<InstalledSefariaBook>(),
            CachedPreview = new CachedSefariaLinkPreview
            {
                Id = fullReference,
                FullReference = fullReference,
                WorkTitle = resolvedWorkTitle,
                WorkHebrewTitle = workHebrewTitle,
                ReferenceWithinWork = referenceWithinWork,
                EnglishText = englishText,
                HebrewText = hebrewText,
                EnglishVersionTitle = FirstNonEmptyPreviewValue(versionTitle, "Sefaria Preview"),
                HebrewVersionTitle = FirstNonEmptyPreviewValue(hebrewVersionTitle, "Sefaria Preview"),
                Categories = categories,
                RetrievedAtUtc = DateTime.UtcNow
            }
        };
    }

    private void SaveCachedLinkPreview(SefariaLinkPreview preview)
    {
        if (preview.CachedPreview is null)
        {
            SaveCachedLinkPreviewRecord(new CachedSefariaLinkPreview
            {
                Id = preview.Reference,
                FullReference = preview.Reference,
                WorkTitle = preview.WorkTitle,
                WorkHebrewTitle = preview.WorkHebrewTitle,
                ReferenceWithinWork = preview.ReferenceWithinWork,
                EnglishText = preview.EnglishText,
                HebrewText = preview.HebrewText,
                RetrievedAtUtc = DateTime.UtcNow
            });
            return;
        }

        SaveCachedLinkPreviewRecord(preview.CachedPreview);
    }

    private void SaveCachedLinkPreviewRecord(CachedSefariaLinkPreview preview)
    {
        using var db = new LiteDatabase(LinksDbPath);
        var collection = db.GetCollection<CachedSefariaLinkPreview>("link_previews");
        collection.EnsureIndex(item => item.FullReference, unique: true);
        collection.Upsert(preview);
    }

    private static SefariaLinkPreview CreateLinkPreviewFromCache(
        CachedSefariaLinkPreview cached,
        string workTitle,
        string fullReference)
    {
        return new SefariaLinkPreview
        {
            Reference = cached.FullReference,
            WorkTitle = FirstNonEmptyPreviewValue(cached.WorkTitle, workTitle),
            WorkHebrewTitle = cached.WorkHebrewTitle,
            ReferenceWithinWork = FirstNonEmptyPreviewValue(
                cached.ReferenceWithinWork,
                NormalizeLinkPreviewReference(ExtractReferenceWithinWork(fullReference, workTitle))),
            EnglishText = FirstNonEmptyPreviewValue(cached.EnglishText),
            HebrewText = FirstNonEmptyPreviewValue(cached.HebrewText),
            IsExcerptOnly = true,
            CachedPreview = cached
        };
    }

    private static List<string> MergePreviewCategories(IEnumerable<string> existing, IEnumerable<string> additional)
    {
        return existing
            .Concat(additional)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryReadLegacyCachedLinkPreviewVersion(
        string filePath,
        out string text,
        out string workHebrewTitle,
        out string versionTitle,
        out List<string> categories)
    {
        text = string.Empty;
        workHebrewTitle = string.Empty;
        versionTitle = string.Empty;
        categories = new List<string>();

        var record = TryCreateInstalledBookFromFile(filePath);
        if (record is null)
        {
            return false;
        }

        var units = ReadInstalledBookUnits(record);
        text = units.FirstOrDefault(unit => !string.IsNullOrWhiteSpace(unit.Text))?.Text ?? string.Empty;
        workHebrewTitle = record.HebrewTitle ?? string.Empty;
        versionTitle = record.VersionTitle;
        categories = new List<string>(record.Categories);
        return !string.IsNullOrWhiteSpace(text);
    }

    private bool TryReadInstalledReferenceText(
        InstalledSefariaBook book,
        string referenceWithinWork,
        out string text)
    {
        text = string.Empty;
        var unit = ReadInstalledBookUnits(book)
            .FirstOrDefault(candidate =>
                string.Equals(
                    NormalizeLinkPreviewReference(candidate.Reference),
                    referenceWithinWork,
                    StringComparison.OrdinalIgnoreCase));
        if (unit is null || string.IsNullOrWhiteSpace(unit.Text))
        {
            return false;
        }

        text = unit.Text;
        return true;
    }

    private string GetLegacyLinkPreviewExcerptFilePath(string fullReference, string languageCode)
    {
        return Path.Combine(
            DataFolder,
            LegacyLinkPreviewFolderName,
            $"{GetSafeFileName(fullReference)}__{languageCode}.json");
    }

    public bool IsLinkPreviewExcerpt(InstalledSefariaBook book)
    {
        if (string.IsNullOrWhiteSpace(book.FilePath))
        {
            return false;
        }

        var excerptFolder = Path.GetFullPath(Path.Combine(DataFolder, LegacyLinkPreviewFolderName));
        var filePath = Path.GetFullPath(book.FilePath);
        return filePath.StartsWith(excerptFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filePath, excerptFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static SefariaVersionOption? SelectPreferredVersion(
        List<SefariaVersionOption> versions,
        CommentaryLanguage preferredLanguage)
    {
        return preferredLanguage == CommentaryLanguage.Hebrew
            ? versions.FirstOrDefault(version => string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase))
                ?? versions.FirstOrDefault()
            : versions.FirstOrDefault(version => !string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase))
                ?? versions.FirstOrDefault();
    }

    private static string GetLinkFullReference(SefariaLinkItem link)
    {
        return FirstNonEmptyPreviewValue(link.SourceRef, link.Ref, link.DisplayReference);
    }

    private static string ResolveLinkWorkTitle(SefariaLinkItem link)
    {
        return FirstNonEmptyPreviewValue(
            link.IndexTitle,
            ExtractReferenceTitle(GetLinkFullReference(link)));
    }

    private static string ExtractReferenceWithinWork(string fullReference, string workTitle)
    {
        if (string.IsNullOrWhiteSpace(fullReference) || string.IsNullOrWhiteSpace(workTitle))
        {
            return string.Empty;
        }

        if (fullReference.StartsWith(workTitle + " ", StringComparison.Ordinal))
        {
            return fullReference[(workTitle.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string ExtractReferenceTitle(string fullReference)
    {
        if (string.IsNullOrWhiteSpace(fullReference))
        {
            return string.Empty;
        }

        var parts = fullReference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var endIndex = parts.Length;
        while (endIndex > 0 && ContainsReferenceDigits(parts[endIndex - 1]))
        {
            endIndex--;
        }

        return endIndex == parts.Length
            ? fullReference.Trim()
            : string.Join(' ', parts.Take(endIndex));
    }

    private static bool ContainsReferenceDigits(string value)
    {
        return value.Any(char.IsDigit);
    }

    private static string NormalizeLinkPreviewReference(string reference)
    {
        return string.IsNullOrWhiteSpace(reference)
            ? string.Empty
            : reference.Trim().Replace(':', '.');
    }

    private static string FirstNonEmptyPreviewValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
