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
    public sealed record InstalledBooksReconciliationResult(
        int Added,
        int Refreshed,
        int Removed,
        int Invalid);

    public bool IsBookDownloaded(SefariaBookNode book)
    {
        var filePath = GetExistingDownloadPath(book);
        return File.Exists(filePath) && IsInstalledBookFile(filePath);
    }

    public string GetExistingDownloadPath(SefariaBookNode book)
    {
        var versionPath = GetBookFilePath(book.Title, book.SelectedVersion);
        if (File.Exists(versionPath))
        {
            return versionPath;
        }

        var legacyPath = GetLegacyBookFilePath(book.Title);
        return File.Exists(legacyPath) ? legacyPath : versionPath;
    }

    public void DeleteBook(SefariaBookNode book)
    {
        var filePath = GetExistingDownloadPath(book);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            RemoveBookJsonCacheEntry(filePath);
        }

        RemoveInstalledBook(book.Title, book.SelectedVersion);
    }


    public List<InstalledSefariaBook> GetInstalledBooks()
    {
        if (!IsConfigured || string.IsNullOrEmpty(SourcesFolder) || !Directory.Exists(SourcesFolder))
        {
            return new List<InstalledSefariaBook>();
        }

        lock (_installedBooksCacheGate)
        {
            if (IsGetInstalledBooksResultCacheValid())
            {
                return CloneInstalledBooks(_getInstalledBooksResultCache!);
            }
        }

        var installed = LoadInstalledBooks();
        var changed = NormalizeInstalledBookManifest(installed, refreshChangedMetadata: false, CancellationToken.None);

        if (changed)
        {
            SaveInstalledBooks(installed);
        }

        var result = SortInstalledBooksForDisplay(installed);

        lock (_installedBooksCacheGate)
        {
            _getInstalledBooksResultCache = CloneInstalledBooks(result);
            UpdateCachedSourcesFolderFingerprint();
        }

        return result;
    }

    public async Task<InstalledBooksReconciliationResult> ReconcileInstalledBooksAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(SourcesFolder) || !Directory.Exists(SourcesFolder))
        {
            return new InstalledBooksReconciliationResult(0, 0, 0, 0);
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = LoadInstalledBooks();
            var beforeKeys = installed.Select(book => book.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var beforeByPath = installed
                .Where(book => !string.IsNullOrWhiteSpace(book.FilePath))
                .GroupBy(book => Path.GetFullPath(book.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var changed = NormalizeInstalledBookManifest(installed, refreshChangedMetadata: true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var afterKeys = installed.Select(book => book.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = beforeKeys.Count(key => !afterKeys.Contains(key));
            var added = afterKeys.Count(key => !beforeKeys.Contains(key));
            var refreshed = installed.Count(book =>
                !string.IsNullOrWhiteSpace(book.FilePath) &&
                beforeByPath.TryGetValue(Path.GetFullPath(book.FilePath), out var previous) &&
                !InstalledBookMetadataMatches(previous, book));

            if (changed)
            {
                SaveInstalledBooks(installed);
            }

            return new InstalledBooksReconciliationResult(added, refreshed, removed, 0);
        }, cancellationToken);
    }

    private bool NormalizeInstalledBookManifest(
        List<InstalledSefariaBook> installed,
        bool refreshChangedMetadata,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var installedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = installed.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = installed[i];
            if (string.IsNullOrWhiteSpace(book.FilePath) ||
                IsReservedSourcesJsonFile(Path.GetFileName(book.FilePath)) ||
                !File.Exists(book.FilePath))
            {
                installed.RemoveAt(i);
                changed = true;
                continue;
            }

            var normalizedBookPath = Path.GetFullPath(book.FilePath);
            if (!string.Equals(book.FilePath, normalizedBookPath, StringComparison.OrdinalIgnoreCase))
            {
                book.FilePath = normalizedBookPath;
                changed = true;
            }

            var fingerprintMatches = InstalledBookFingerprintMatchesFile(book);
            if (!fingerprintMatches && refreshChangedMetadata)
            {
                var refreshed = TryCreateInstalledBookFromFile(book.FilePath, cacheJson: false);
                if (refreshed is not null)
                {
                    refreshed.LastScrollOffset = book.LastScrollOffset;
                    installed[i] = refreshed;
                    book = refreshed;
                    changed = true;
                }
                else
                {
                    installed.RemoveAt(i);
                    changed = true;
                    continue;
                }
            }

            installedPaths.Add(book.FilePath);
        }

        foreach (var filePath in Directory.EnumerateFiles(SourcesFolder, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(filePath);
            if (IsReservedSourcesJsonFile(fileName))
            {
                continue;
            }

            var normalizedPath = Path.GetFullPath(filePath);
            if (installedPaths.Contains(normalizedPath))
            {
                continue;
            }

            if (!refreshChangedMetadata)
            {
                continue;
            }

            var inferred = TryCreateInstalledBookFromFile(normalizedPath, cacheJson: false);
            if (inferred is not null)
            {
                installed.Add(inferred);
                installedPaths.Add(inferred.FilePath);
                changed = true;
            }
        }

        var deduplicated = installed
            .GroupBy(book => book.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                first.LastScrollOffset = group.Max(book => book.LastScrollOffset);
                return first;
            })
            .ToList();
        if (deduplicated.Count != installed.Count)
        {
            installed.Clear();
            installed.AddRange(deduplicated);
            changed = true;
        }

        return changed;
    }

    private static List<InstalledSefariaBook> SortInstalledBooksForDisplay(IEnumerable<InstalledSefariaBook> installed)
    {
        return installed
            .OrderBy(book => string.Join("/", book.Categories), StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ObservableCollection<object> BuildInstalledTree()
    {
        var roots = new ObservableCollection<object>();
        var orderLookup = BuildIndexOrderLookup();
        var installedBooks = GetInstalledBooks()
            .Select(book => ApplyIndexOrder(book, orderLookup))
            .OrderBy(book => book.CategoryOrders.ElementAtOrDefault(0))
            .ThenBy(book => book.CategoryOrders.ElementAtOrDefault(1))
            .ThenBy(book => book.CategoryOrders.ElementAtOrDefault(2))
            .ThenBy(book => book.Order)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var book in installedBooks)
        {
            var current = roots;
            var categoryPath = new List<string>();
            for (var i = 0; i < book.Categories.Count; i++)
            {
                var categoryName = book.Categories[i];
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    continue;
                }

                categoryPath.Add(categoryName);
                var categoryOrder = book.CategoryOrders.ElementAtOrDefault(i);
                var hebrewCategoryName = book.HebrewCategories.ElementAtOrDefault(i);
                var category = current.OfType<InstalledSefariaCategory>()
                    .FirstOrDefault(c => string.Equals(c.Title, categoryName, StringComparison.OrdinalIgnoreCase));
                if (category is null)
                {
                    category = new InstalledSefariaCategory
                    {
                        Title = categoryName,
                        HebrewTitle = hebrewCategoryName,
                        CategoryPath = string.Join("/", categoryPath),
                        Order = categoryOrder
                    };
                    current.Add(category);
                }
                else if (string.IsNullOrWhiteSpace(category.CategoryPath))
                {
                    category.CategoryPath = string.Join("/", categoryPath);
                }

                current = category.Children;
            }

            var bookCategory = current.OfType<InstalledSefariaCategory>()
                .FirstOrDefault(c => string.Equals(c.Title, book.Title, StringComparison.OrdinalIgnoreCase));
            if (bookCategory is null)
            {
                bookCategory = new InstalledSefariaCategory
                {
                    Title = book.Title,
                    HebrewTitle = book.HebrewTitle,
                    CategoryPath = string.Join("/", categoryPath.Append(book.Title)),
                    IsBookTitle = true,
                    Order = book.Order
                };
                current.Add(bookCategory);
            }
        }

        SortInstalledTree(roots);
        return roots;
    }

    public void SaveReadingPosition(InstalledSefariaBook book, double verticalOffset)
    {
        var installed = LoadInstalledBooks();
        var existing = installed.FirstOrDefault(item => string.Equals(item.Key, book.Key, StringComparison.Ordinal)) ??
            installed.FirstOrDefault(item => string.Equals(item.FilePath, book.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        existing.LastScrollOffset = verticalOffset;
        SaveInstalledBooks(installed);
        book.LastScrollOffset = verticalOffset;
    }

    public InstalledSefariaBook? GetInstalledBookByKey(string key)
    {
        return GetInstalledBooks().FirstOrDefault(book => string.Equals(book.Key, key, StringComparison.Ordinal));
    }

    public List<InstalledSefariaBook> GetInstalledVersionsForTitle(string title)
    {
        return GetInstalledBooks()
            .Where(book => string.Equals(book.Title, title, StringComparison.Ordinal))
            .SelectMany(ExpandTalmudBilingualVersions)
            .GroupBy(book => book.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(IsHebrew)
            .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Dictionary<string, List<InstalledSefariaBook>> GetInstalledVersionsByTitle(
        IReadOnlyList<InstalledSefariaBook> installedBooks)
    {
        return installedBooks
            .SelectMany(ExpandTalmudBilingualVersions)
            .GroupBy(book => book.Title, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(book => book.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(versionGroup => versionGroup.First())
                    .OrderByDescending(IsHebrew)
                    .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.Ordinal);
    }

    public List<InstalledSefariaBook> GetInstalledVersionSummariesForTitle(string title)
    {
        return LoadInstalledBooks()
            .Where(book => string.Equals(book.Title, title, StringComparison.Ordinal))
            .GroupBy(book => book.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(IsHebrew)
            .ThenBy(book => book.VersionTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<InstalledSefariaBook> ExpandTalmudBilingualVersions(InstalledSefariaBook book)
    {
        if (!IsTalmud(book))
        {
            yield return book;
            yield break;
        }

        var json = GetCachedBookJsonText(book.FilePath);
        if (json is null)
        {
            yield return book;
            yield break;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            yield return book;
            yield break;
        }

        var sampleRoot = GetMetadataRoot(root);
        var yielded = false;

        if (sampleRoot.TryGetProperty("he", out var heElement) &&
            heElement.ValueKind == JsonValueKind.Array &&
            heElement.GetArrayLength() > 0)
        {
            yielded = true;
            yield return CreateTalmudVersionFromSharedFile(book, sampleRoot, "he");
        }

        if (sampleRoot.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.Array &&
            textElement.GetArrayLength() > 0)
        {
            yielded = true;
            yield return CreateTalmudVersionFromSharedFile(book, sampleRoot, "en");
        }

        if (!yielded)
        {
            yield return book;
        }
    }

    private static InstalledSefariaBook CreateTalmudVersionFromSharedFile(
        InstalledSefariaBook source,
        JsonElement root,
        string languageCode)
    {
        var version = new InstalledSefariaBook
        {
            Title = source.Title,
            HebrewTitle = source.HebrewTitle,
            Categories = new List<string>(source.Categories),
            HebrewCategories = new List<string?>(source.HebrewCategories),
            CategoryOrders = new List<float>(source.CategoryOrders),
            Order = source.Order,
            LanguageCode = languageCode,
            VersionTitle = source.VersionTitle,
            FilePath = source.FilePath,
            LastScrollOffset = source.LastScrollOffset
        };

        if (languageCode == "he")
        {
            if (root.TryGetProperty("heVersionTitle", out var heVersionTitle) &&
                !string.IsNullOrWhiteSpace(heVersionTitle.GetString()))
            {
                version.VersionTitle = heVersionTitle.GetString()!;
            }
            else
            {
                version.VersionTitle = "Hebrew";
            }
        }
        else if (root.TryGetProperty("versionTitle", out var versionTitle) &&
            !string.IsNullOrWhiteSpace(versionTitle.GetString()))
        {
            version.VersionTitle = versionTitle.GetString()!;
        }

        return version;
    }

    private string GetBookFilePath(string title, SefariaVersionOption? version)
    {
        var safeTitle = GetSafeFileName(title);
        var versionSegment = version is null
            ? "default"
            : GetSafeFileName($"{version.LanguageCode}_{version.VersionTitle}");

        return Path.Combine(SourcesFolder, $"{safeTitle}__{versionSegment}.json");
    }

    private string GetLegacyBookFilePath(string title)
    {
        return Path.Combine(SourcesFolder, $"{GetSafeFileName(title)}.json");
    }

    private static string GetSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    private Dictionary<string, InstalledIndexOrder> BuildIndexOrderLookup()
    {
        try
        {
            if (!File.Exists(IndexFilePath))
            {
                return new Dictionary<string, InstalledIndexOrder>(StringComparer.OrdinalIgnoreCase);
            }

            var lastWrite = File.GetLastWriteTimeUtc(IndexFilePath);
            lock (_installedBooksCacheGate)
            {
                if (_indexOrderLookupCache is not null && _indexFileLastWriteUtc == lastWrite)
                {
                    return _indexOrderLookupCache;
                }
            }

            var indexText = ReadJsonTextFile(IndexFilePath);
            var indexNodes = JsonSerializer.Deserialize<List<SefariaIndexJsonNode>>(indexText) ?? new List<SefariaIndexJsonNode>();
            var lookup = new Dictionary<string, InstalledIndexOrder>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in indexNodes)
            {
                AddIndexOrder(node, new List<string>(), new List<string?>(), new List<float>(), lookup);
            }

            lock (_installedBooksCacheGate)
            {
                _indexOrderLookupCache = lookup;
                _indexFileLastWriteUtc = lastWrite;
            }

            return lookup;
        }
        catch
        {
            return new Dictionary<string, InstalledIndexOrder>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AddIndexOrder(
        SefariaIndexJsonNode node,
        List<string> categoryPath,
        List<string?> hebrewCategoryPath,
        List<float> categoryOrders,
        Dictionary<string, InstalledIndexOrder> lookup)
    {
        if (node.Contents is { Count: > 0 })
        {
            var category = string.IsNullOrWhiteSpace(node.Category) ? node.Title : node.Category;
            var nextPath = new List<string>(categoryPath);
            var nextHebrewPath = new List<string?>(hebrewCategoryPath);
            var nextOrders = new List<float>(categoryOrders);
            if (!string.IsNullOrWhiteSpace(category))
            {
                nextPath.Add(category);
                nextHebrewPath.Add(node.HebrewCategory);
                nextOrders.Add(node.Order);
            }

            foreach (var child in node.Contents)
            {
                AddIndexOrder(child, nextPath, nextHebrewPath, nextOrders, lookup);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            lookup[node.Title] = new InstalledIndexOrder(
                new List<string>(categoryPath),
                new List<string?>(hebrewCategoryPath),
                new List<float>(categoryOrders),
                node.Order);
        }
    }

    private static InstalledSefariaBook ApplyIndexOrder(
        InstalledSefariaBook book,
        Dictionary<string, InstalledIndexOrder> orderLookup)
    {
        if (!orderLookup.TryGetValue(book.Title, out var order))
        {
            return book;
        }

        book.Categories = new List<string>(order.Categories);
        book.HebrewCategories = new List<string?>(order.HebrewCategories);
        book.CategoryOrders = new List<float>(order.CategoryOrders);
        book.Order = order.Order;
        return book;
    }

    private static bool InstalledBookMetadataMatches(InstalledSefariaBook current, InstalledSefariaBook refreshed)
    {
        return string.Equals(current.Title, refreshed.Title, StringComparison.Ordinal) &&
            string.Equals(current.HebrewTitle, refreshed.HebrewTitle, StringComparison.Ordinal) &&
            string.Equals(current.LanguageCode, refreshed.LanguageCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.VersionTitle, refreshed.VersionTitle, StringComparison.Ordinal) &&
            current.Categories.SequenceEqual(refreshed.Categories, StringComparer.Ordinal) &&
            current.HebrewCategories.SequenceEqual(refreshed.HebrewCategories, StringComparer.Ordinal) &&
            current.CategoryOrders.SequenceEqual(refreshed.CategoryOrders) &&
            Math.Abs(current.Order - refreshed.Order) < 0.001;
    }

    private static void SortInstalledTree(ObservableCollection<object> nodes)
    {
        var sorted = nodes
            .OrderBy(GetInstalledTreeOrder)
            .ThenBy(GetInstalledTreeTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        nodes.Clear();
        foreach (var node in sorted)
        {
            if (node is InstalledSefariaCategory category)
            {
                SortInstalledTree(category.Children);
            }

            nodes.Add(node);
        }
    }

    private static float GetInstalledTreeOrder(object node)
    {
        return node switch
        {
            InstalledSefariaCategory category => category.Order,
            InstalledSefariaBook book => book.Order,
            _ => 0
        };
    }

    private static string GetInstalledTreeTitle(object node)
    {
        return node switch
        {
            InstalledSefariaCategory category => category.Title,
            InstalledSefariaBook book => book.VersionTitle,
            _ => string.Empty
        };
    }

    private sealed record InstalledIndexOrder(
        List<string> Categories,
        List<string?> HebrewCategories,
        List<float> CategoryOrders,
        float Order);

    private List<InstalledSefariaBook> LoadInstalledBooks(bool forceRefresh = false)
    {
        var database = _installedBooksDatabase;
        if (database is null)
        {
            return new List<InstalledSefariaBook>();
        }

        lock (_installedBooksCacheGate)
        {
            var manifestLastWrite = database.GetLastWriteTimeUtc();

            if (!forceRefresh &&
                _installedBooksCache is not null &&
                _installedBooksManifestLastWriteUtc == manifestLastWrite)
            {
                return CloneInstalledBooks(_installedBooksCache);
            }

            List<InstalledSefariaBook> installed;
            try
            {
                installed = database.Read();
            }
            catch
            {
                installed = new List<InstalledSefariaBook>();
            }

            _installedBooksManifestLastWriteUtc = manifestLastWrite;
            _installedBooksCache = CloneInstalledBooks(installed);
            return CloneInstalledBooks(_installedBooksCache);
        }
    }

    private void SaveInstalledBooks(List<InstalledSefariaBook> installed)
    {
        var database = _installedBooksDatabase
            ?? throw new InvalidOperationException("The installed-books database is not configured.");
        var lastWriteUtc = database.Write(installed);
        lock (_installedBooksCacheGate)
        {
            _installedBooksManifestLastWriteUtc = lastWriteUtc;
            _installedBooksCache = CloneInstalledBooks(installed);
            _getInstalledBooksResultCache = null;
        }
    }

    private void UpsertInstalledBook(InstalledSefariaBook book)
    {
        lock (_installedBooksCacheGate)
        {
            var installed = LoadInstalledBooks();
            var existing = installed.FindIndex(item => string.Equals(item.Key, book.Key, StringComparison.Ordinal));
            if (existing >= 0)
            {
                book.LastScrollOffset = installed[existing].LastScrollOffset;
                installed[existing] = book;
            }
            else
            {
                installed.Add(book);
            }

            SaveInstalledBooks(installed);
        }
    }

    private void RemoveInstalledBook(string title, SefariaVersionOption? version)
    {
        lock (_installedBooksCacheGate)
        {
            var key = $"{title}|{version?.LanguageCode ?? "en"}|{version?.VersionTitle ?? string.Empty}";
            var installed = LoadInstalledBooks();
            installed.RemoveAll(book =>
                string.Equals(book.Key, key, StringComparison.Ordinal) ||
                (string.Equals(book.Title, title, StringComparison.Ordinal) &&
                 string.Equals(book.LanguageCode, version?.LanguageCode ?? "en", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(book.VersionTitle, version?.VersionTitle ?? string.Empty, StringComparison.Ordinal)));
            SaveInstalledBooks(installed);
        }
    }

    private InstalledSefariaBook CreateInstalledBookRecord(SefariaBookNode book, string filePath, string json)
    {
        var record = new InstalledSefariaBook
        {
            Title = book.Title,
            HebrewTitle = book.HebrewTitle,
            Categories = book.Categories,
            LanguageCode = book.SelectedVersion?.LanguageCode ?? "en",
            VersionTitle = book.SelectedVersion?.VersionTitle ?? string.Empty,
            FilePath = filePath
        };

        using var document = JsonDocument.Parse(json);
        ApplyJsonMetadata(record, document.RootElement);
        ApplyFileFingerprint(record);
        SeedBookJsonCache(filePath, json, record);
        return record;
    }

    private InstalledSefariaBook? TryCreateInstalledBookFromFile(string filePath, bool cacheJson = true)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                RemoveBookJsonCacheEntry(filePath);
                return null;
            }

            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            lock (_installedBooksCacheGate)
            {
                if (_bookJsonCache.TryGetValue(filePath, out var cached) &&
                    cached.LastWriteTimeUtc == lastWrite)
                {
                    return cached.Book is null ? null : CloneInstalledBook(cached.Book);
                }
            }

            var json = ReadJsonTextFile(filePath);
            var book = TryCreateInstalledBookFromJson(json, filePath);
            if (book is not null)
            {
                ApplyFileFingerprint(book);
            }

            if (cacheJson)
            {
                CacheBookJsonEntry(filePath, lastWrite, json, book);
            }

            return book;
        }
        catch
        {
            return null;
        }
    }

    private static InstalledSefariaBook? TryCreateInstalledBookFromJson(string json, string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!IsDownloadedBookRoot(root))
            {
                return null;
            }

            var record = new InstalledSefariaBook
            {
                FilePath = filePath
            };
            ApplyFileNameMetadata(record, filePath);
            ApplyJsonMetadata(record, root);
            ApplyFileFingerprint(record);
            return string.IsNullOrWhiteSpace(record.Title) ? null : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool InstalledBookFingerprintMatchesFile(InstalledSefariaBook book)
    {
        if (string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath))
        {
            return false;
        }

        var info = new FileInfo(book.FilePath);
        return book.FileLength == info.Length &&
            book.FileLastWriteTimeUtc == info.LastWriteTimeUtc;
    }

    private static bool ApplyFileFingerprint(InstalledSefariaBook book)
    {
        if (string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath))
        {
            return false;
        }

        var info = new FileInfo(book.FilePath);
        var fullPath = info.FullName;
        var changed = !string.Equals(book.FilePath, fullPath, StringComparison.OrdinalIgnoreCase) ||
            book.FileLength != info.Length ||
            book.FileLastWriteTimeUtc != info.LastWriteTimeUtc;

        book.FilePath = fullPath;
        book.FileLength = info.Length;
        book.FileLastWriteTimeUtc = info.LastWriteTimeUtc;
        return changed;
    }

    private bool IsInstalledBookFile(string filePath)
    {
        try
        {
            return TryCreateInstalledBookFromFile(filePath) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateDownloadedBookJson(string json, string title, SefariaVersionOption? version)
    {
        if (IsDownloadedBookJson(json))
        {
            return;
        }

        var versionTitle = version?.VersionTitle;
        var label = string.IsNullOrWhiteSpace(versionTitle)
            ? title
            : $"{title} ({versionTitle})";
        throw new InvalidOperationException($"Sefaria did not return downloadable JSON for {label}.");
    }

    private static bool IsDownloadedBookJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return IsDownloadedBookRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsDownloadedBookRoot(JsonElement root)
    {
        var hasText = root.TryGetProperty("text", out var text) &&
            (text.ValueKind == JsonValueKind.Array ||
             HasArrayTextChild(text));
        var hasPages = root.TryGetProperty("pages", out var pages) &&
            pages.ValueKind == JsonValueKind.Array &&
            pages.GetArrayLength() > 0;

        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("title", out var title) &&
            !string.IsNullOrWhiteSpace(title.GetString()) &&
            (hasText || hasPages);
    }

    private static bool HasArrayTextChild(JsonElement text)
    {
        if (text.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (text.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (text.TryGetProperty(string.Empty, out var defaultText) &&
            defaultText.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (text.TryGetProperty("default", out defaultText) &&
            defaultText.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return text.EnumerateObject().Any(property => HasArrayTextChild(property.Value));
    }

    private static void ApplyJsonMetadata(InstalledSefariaBook record, JsonElement root)
    {
        var metadataRoot = GetMetadataRoot(root);

        if (metadataRoot.TryGetProperty("indexTitle", out var indexTitle))
        {
            record.Title = indexTitle.GetString() ?? record.Title;
        }
        else if (metadataRoot.TryGetProperty("book", out var book))
        {
            record.Title = book.GetString() ?? record.Title;
        }
        else if (metadataRoot.TryGetProperty("title", out var title))
        {
            record.Title = title.GetString() ?? record.Title;
        }

        if (metadataRoot.TryGetProperty("heIndexTitle", out var heIndexTitle))
        {
            record.HebrewTitle = heIndexTitle.GetString();
        }
        else if (metadataRoot.TryGetProperty("heBook", out var heBook))
        {
            record.HebrewTitle = heBook.GetString();
        }
        else if (metadataRoot.TryGetProperty("heTitle", out var heTitle))
        {
            record.HebrewTitle = heTitle.GetString();
        }

        if (metadataRoot.TryGetProperty("language", out var language))
        {
            record.LanguageCode = NormalizeDownloadedLanguageCode(language.GetString(), record.LanguageCode);
        }

        if (IsHebrew(record) && metadataRoot.TryGetProperty("heVersionTitle", out var heVersionTitle))
        {
            record.VersionTitle = heVersionTitle.GetString() ?? record.VersionTitle;
        }
        else if (metadataRoot.TryGetProperty("versionTitle", out var versionTitle))
        {
            record.VersionTitle = versionTitle.GetString() ?? record.VersionTitle;
        }

        if (metadataRoot.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            record.Categories = categories.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }
    }

    private static JsonElement GetMetadataRoot(JsonElement root)
    {
        if (root.TryGetProperty("pages", out var pages) &&
            pages.ValueKind == JsonValueKind.Array &&
            pages.GetArrayLength() > 0 &&
            pages[0].ValueKind == JsonValueKind.Object)
        {
            return pages[0];
        }

        return root;
    }

    private static string NormalizeDownloadedLanguageCode(string? language, string fallback)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return fallback;
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "hebrew" => "he",
            "english" => "en",
            var code when code.Length >= 2 => code[..2],
            _ => fallback
        };
    }

    private static void ApplyFileNameMetadata(InstalledSefariaBook record, string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var separator = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separator < 0 || separator + 2 >= fileName.Length)
        {
            return;
        }

        var versionSegment = fileName[(separator + 2)..];
        if (string.Equals(versionSegment, "default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var languageSeparator = versionSegment.IndexOf('_');
        if (languageSeparator <= 0 || languageSeparator + 1 >= versionSegment.Length)
        {
            return;
        }

        record.LanguageCode = versionSegment[..languageSeparator].Trim().ToLowerInvariant();
        record.VersionTitle = versionSegment[(languageSeparator + 1)..].Trim();
    }

    private void InvalidateInstalledBooksCaches()
    {
        lock (_installedBooksCacheGate)
        {
            _installedBooksCache = null;
            _getInstalledBooksResultCache = null;
            _installedBooksManifestLastWriteUtc = default;
            _cachedSourcesBookFileCount = 0;
            _cachedSourcesMaxLastWriteUtc = default;
            _indexOrderLookupCache = null;
            _indexFileLastWriteUtc = default;
            _bookJsonCache.Clear();
        }
    }

    private bool IsGetInstalledBooksResultCacheValid()
    {
        if (_getInstalledBooksResultCache is null)
        {
            return false;
        }

        GetSourcesFolderFingerprint(out var count, out var maxLastWriteUtc);
        return count == _cachedSourcesBookFileCount &&
            maxLastWriteUtc == _cachedSourcesMaxLastWriteUtc;
    }

    private void UpdateCachedSourcesFolderFingerprint()
    {
        GetSourcesFolderFingerprint(out _cachedSourcesBookFileCount, out _cachedSourcesMaxLastWriteUtc);
    }

    private void GetSourcesFolderFingerprint(out int count, out DateTime maxLastWriteUtc)
    {
        count = 0;
        maxLastWriteUtc = DateTime.MinValue;
        if (!Directory.Exists(SourcesFolder))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(SourcesFolder, "*.json"))
        {
            if (IsReservedSourcesJsonFile(Path.GetFileName(filePath)))
            {
                continue;
            }

            count++;
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (lastWrite > maxLastWriteUtc)
            {
                maxLastWriteUtc = lastWrite;
            }
        }
    }

    private static bool IsReservedSourcesJsonFile(string fileName)
    {
        return string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, BooksManifestFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, InstalledBooksFileName, StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveBookJsonCacheEntry(string filePath)
    {
        lock (_installedBooksCacheGate)
        {
            _bookJsonCache.Remove(filePath);
            _getInstalledBooksResultCache = null;
        }
    }

    private void SeedBookJsonCache(string filePath, string json, InstalledSefariaBook book)
    {
        var lastWrite = File.Exists(filePath)
            ? File.GetLastWriteTimeUtc(filePath)
            : DateTime.UtcNow;
        CacheBookJsonEntry(filePath, lastWrite, json, book);
        lock (_installedBooksCacheGate)
        {
            _getInstalledBooksResultCache = null;
        }
    }

    private void CacheBookJsonEntry(string filePath, DateTime lastWrite, string json, InstalledSefariaBook? book)
    {
        lock (_installedBooksCacheGate)
        {
            _bookJsonCache[filePath] = new BookJsonCacheEntry
            {
                LastWriteTimeUtc = lastWrite,
                Json = json,
                Book = book is null ? null : CloneInstalledBook(book)
            };
        }
    }

    private string? GetCachedBookJsonText(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            lock (_installedBooksCacheGate)
            {
                if (_bookJsonCache.TryGetValue(filePath, out var cached) &&
                    cached.LastWriteTimeUtc == lastWrite)
                {
                    return cached.Json;
                }
            }

            _ = TryCreateInstalledBookFromFile(filePath);
            lock (_installedBooksCacheGate)
            {
                if (_bookJsonCache.TryGetValue(filePath, out var cached) &&
                    cached.LastWriteTimeUtc == lastWrite)
                {
                    return cached.Json;
                }
            }

            return ReadJsonTextFile(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static InstalledSefariaBook CloneInstalledBook(InstalledSefariaBook book)
    {
        return new InstalledSefariaBook
        {
            Title = book.Title,
            HebrewTitle = book.HebrewTitle,
            Categories = new List<string>(book.Categories),
            HebrewCategories = new List<string?>(book.HebrewCategories),
            CategoryOrders = new List<float>(book.CategoryOrders),
            Order = book.Order,
            LanguageCode = book.LanguageCode,
            VersionTitle = book.VersionTitle,
            FilePath = book.FilePath,
            FileLength = book.FileLength,
            FileLastWriteTimeUtc = book.FileLastWriteTimeUtc,
            LastScrollOffset = book.LastScrollOffset
        };
    }

    private static List<InstalledSefariaBook> CloneInstalledBooks(IEnumerable<InstalledSefariaBook> installed)
    {
        return installed.Select(CloneInstalledBook).ToList();
    }
}
