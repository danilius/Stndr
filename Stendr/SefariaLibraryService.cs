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

namespace Stendr;

public sealed class SefariaLibraryService
{
    private const string SefariaFolderName = "Sefaria";
    private const string IndexFileName = "sefaria_index.json";
    private const string InstalledBooksFileName = "installed-books.json";
    private const string CommentariesDbFileName = "commentaries.db";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim CommentaryCacheGate = new(1, 1);

    public SefariaLibraryService()
    {
        ProjectFolder = ResolveProjectFolder();
        DataFolder = Path.Combine(ProjectFolder, "Data", SefariaFolderName);
        IndexFilePath = Path.Combine(DataFolder, IndexFileName);
        InstalledBooksFilePath = Path.Combine(DataFolder, InstalledBooksFileName);
        CommentariesDbPath = Path.Combine(DataFolder, CommentariesDbFileName);
        Directory.CreateDirectory(DataFolder);
    }

    public string ProjectFolder { get; }
    public string DataFolder { get; }
    public string IndexFilePath { get; }
    public string InstalledBooksFilePath { get; }
    public string CommentariesDbPath { get; }

    public async Task<SefariaCategoryNode> LoadLibraryAsync(CancellationToken cancellationToken)
    {
        var indexText = await EnsureIndexAvailableAsync(cancellationToken);
        var indexNodes = JsonSerializer.Deserialize<List<SefariaIndexJsonNode>>(indexText) ?? new List<SefariaIndexJsonNode>();
        var root = new SefariaCategoryNode
        {
            Category = "Sefaria",
            Contents = new ObservableCollection<SefariaNode>()
        };

        foreach (var node in indexNodes)
        {
            AddLibraryNode(node, root);
        }

        return root;
    }

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
        }

        RemoveInstalledBook(book.Title, book.SelectedVersion);
    }

    public async Task<List<SefariaVersionOption>> GetAvailableVersionsAsync(string title, CancellationToken cancellationToken)
    {
        var versions = new List<SefariaVersionOption>();
        var sectionRef = GetVersionProbeRef(title);
        var url = $"https://www.sefaria.org/api/v3/texts/{Uri.EscapeDataString(sectionRef)}?version=all";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("available_versions", out var availableVersions) ||
            availableVersions.ValueKind != JsonValueKind.Array)
        {
            return versions;
        }

        foreach (var item in availableVersions.EnumerateArray())
        {
            if (!item.TryGetProperty("versionTitle", out var versionTitleElement))
            {
                continue;
            }

            var versionTitle = versionTitleElement.GetString();
            if (string.IsNullOrWhiteSpace(versionTitle))
            {
                continue;
            }

            var languageCode = item.TryGetProperty("language", out var langElement)
                ? langElement.GetString()
                : null;

            var languageFamilyName = item.TryGetProperty("languageFamilyName", out var familyElement)
                ? familyElement.GetString()
                : null;

            languageCode = NormalizeLanguageCode(languageCode, languageFamilyName);

            versions.Add(new SefariaVersionOption
            {
                LanguageCode = languageCode,
                LanguageFamilyName = languageFamilyName,
                VersionTitle = versionTitle,
                DisplayText = $"{versionTitle} ({languageCode})"
            });
        }

        return versions
            .GroupBy(v => $"{v.LanguageCode}|{v.VersionTitle}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task DownloadBookAsync(
        SefariaBookNode book,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var content = IsTalmudTitle(book.Title)
            ? await DownloadTalmudTractateAsync(book.Title, book.SelectedVersion, progress, cancellationToken)
            : await DownloadWithRetryAsync(book.Title, book.SelectedVersion, progress, cancellationToken);
        ValidateDownloadedBookJson(content, book.Title, book.SelectedVersion);
        var filePath = GetBookFilePath(book.Title, book.SelectedVersion);
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);
        UpsertInstalledBook(CreateInstalledBookRecord(book, filePath, content));
    }

    public List<InstalledSefariaBook> GetInstalledBooks()
    {
        var installed = LoadInstalledBooks();
        var changed = false;

        foreach (var filePath in Directory.EnumerateFiles(DataFolder, "*.json"))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, InstalledBooksFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (installed.Any(book => string.Equals(book.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var inferred = TryCreateInstalledBookFromFile(filePath);
            if (inferred is not null)
            {
                installed.Add(inferred);
                changed = true;
            }
        }

        installed.RemoveAll(book => string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath));
        for (var i = 0; i < installed.Count; i++)
        {
            var refreshed = TryCreateInstalledBookFromFile(installed[i].FilePath);
            if (refreshed is null)
            {
                continue;
            }

            refreshed.LastScrollOffset = installed[i].LastScrollOffset;
            if (!InstalledBookMetadataMatches(installed[i], refreshed))
            {
                installed[i] = refreshed;
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
            installed = deduplicated;
            changed = true;
        }

        if (changed)
        {
            SaveInstalledBooks(installed);
        }

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
            for (var i = 0; i < book.Categories.Count; i++)
            {
                var categoryName = book.Categories[i];
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    continue;
                }

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
                        Order = categoryOrder
                    };
                    current.Add(category);
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
                    IsBookTitle = true,
                    Order = book.Order
                };
                current.Add(bookCategory);
            }
        }

        SortInstalledTree(roots);
        return roots;
    }

    public string ReadInstalledBookText(InstalledSefariaBook book)
    {
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("text", out var textElement))
        {
            return json;
        }

        var lines = new List<string>();
        AppendTextElement(textElement, lines, 1);
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
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

    public List<ReaderTextUnit> ReadInstalledBookUnits(InstalledSefariaBook book)
    {
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (IsTalmud(book))
        {
            return ReadTalmudTextUnits(book, root);
        }

        if (!root.TryGetProperty("text", out var textElement))
        {
            return new List<ReaderTextUnit>
            {
                new("1", json)
            };
        }

        var units = new List<ReaderTextUnit>();
        if (IsMishnah(book))
        {
            AppendMishnahTextUnits(textElement, units);
            return units;
        }

        AppendTextUnits(textElement, units, new List<int>());
        return units;
    }

    public async Task<List<SefariaCommentaryItem>> GetCommentariesAsync(
        string anchorRef,
        CancellationToken cancellationToken)
    {
        anchorRef = NormalizeCommentaryAnchorRef(anchorRef);
        if (string.IsNullOrWhiteSpace(anchorRef))
        {
            return new List<SefariaCommentaryItem>();
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

    public List<ReaderNavigationPage> ReadInstalledBookNavigationPages(InstalledSefariaBook book)
    {
        var json = ReadJsonTextFile(book.FilePath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!IsTalmud(book) ||
            !root.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array)
        {
            return ReadDirectTalmudNavigationPages(book, root);
        }

        var navigationPages = new List<ReaderNavigationPage>();
        var chapterTitle = string.Empty;
        var hebrewChapterTitle = string.Empty;
        foreach (var pageRoot in pages.EnumerateArray())
        {
            var page = GetTalmudPage(pageRoot);
            if (string.IsNullOrWhiteSpace(page))
            {
                continue;
            }

            var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
            if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
            {
                chapterTitle = pageChapterTitles.ChapterTitle;
                hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
            }

            navigationPages.Add(new ReaderNavigationPage(page, chapterTitle, hebrewChapterTitle));
        }

        return navigationPages;
    }

    private static List<ReaderNavigationPage> ReadDirectTalmudNavigationPages(InstalledSefariaBook book, JsonElement root)
    {
        if (!IsTalmud(book) ||
            !root.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.Array ||
            !textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            return new List<ReaderNavigationPage>();
        }

        var navigationPages = new List<ReaderNavigationPage>();
        var address = 0;
        foreach (var _ in textElement.EnumerateArray())
        {
            navigationPages.Add(new ReaderNavigationPage(FormatTalmudPageFromAddress(address), string.Empty, string.Empty));
            address++;
        }

        return navigationPages;
    }

    public static bool IsHebrew(InstalledSefariaBook book)
    {
        return string.Equals(book.LanguageCode, "he", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMishnah(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Mishnah", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTalmud(InstalledSefariaBook book)
    {
        return book.Categories.Any(category => string.Equals(category, "Talmud", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<InstalledSefariaBook> ExpandTalmudBilingualVersions(InstalledSefariaBook book)
    {
        if (!IsTalmud(book))
        {
            yield return book;
            yield break;
        }

        using var document = JsonDocument.Parse(ReadJsonTextFile(book.FilePath));
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

    private async Task<string> EnsureIndexAvailableAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(IndexFilePath))
        {
            var content = await File.ReadAllTextAsync(IndexFilePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "https://www.sefaria.org/api/index/");
        request.Headers.Add("accept", "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var indexData = await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(IndexFilePath, indexData, Encoding.UTF8, cancellationToken);
        return indexData;
    }

    private async Task<string> DownloadWithRetryAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var delayMs = 1000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await DownloadTextWithProgressAsync(title, version, progress, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
            }
        }

        throw new InvalidOperationException("Download failed after multiple attempts.");
    }



    private async Task<string> DownloadTalmudPageWithTextApiAsync(
        string title,
        SefariaVersionOption version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var reference = GetVersionProbeRef(title);
        var versionParameter = GetTextApiVersionParameter(version);
        var url = $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0&{versionParameter}={Uri.EscapeDataString(version.VersionTitle)}";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        progress.Report(0);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        progress.Report(100);
        return content;
    }

    private async Task<string> DownloadTalmudTractateAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<JsonElement>();
        var reference = GetVersionProbeRef(title);
        var totalPages = 0;
        var downloadedCount = 0;
        progress.Report(0);

        while (!string.IsNullOrWhiteSpace(reference))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageJson = version is null
                ? await DownloadTextApiPageAsync(reference, null, cancellationToken)
                : await DownloadTextApiPageAsync(reference, version, cancellationToken);

            using var document = JsonDocument.Parse(pageJson);
            var pageRoot = document.RootElement.Clone();
            pages.Add(pageRoot);
            downloadedCount++;

            // Try to get total page count from the first page
            if (totalPages == 0 &&
                pageRoot.TryGetProperty("length", out var lengthElement) &&
                lengthElement.TryGetInt32(out var length))
            {
                totalPages = length;
            }

            // Report progress
            if (totalPages > 0)
            {
                progress.Report(Math.Min(99, ((double)downloadedCount / totalPages) * 100));
            }
            else
            {
                // If we don't know total, estimate conservatively
                progress.Report(Math.Min(99, downloadedCount * 8));
            }

            reference = pageRoot.TryGetProperty("next", out var nextElement)
                ? nextElement.GetString() ?? string.Empty
                : string.Empty;
        }

        progress.Report(100);
        return CreateTalmudTractateJson(title, version, pages);
    }

    private static async Task<string> DownloadTextApiPageAsync(
        string reference,
        SefariaVersionOption? version,
        CancellationToken cancellationToken)
    {
        var url = version is null
            ? $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0"
            : $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0&{GetTextApiVersionParameter(version)}={Uri.EscapeDataString(version.VersionTitle)}";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string GetTextApiVersionParameter(SefariaVersionOption version)
    {
        return string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase) ? "vhe" : "ven";
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

    private static string CreateTalmudTractateJson(
        string title,
        SefariaVersionOption? version,
        List<JsonElement> pages)
    {
        var firstPage = pages.FirstOrDefault();
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["indexTitle"] = title,
            ["pages"] = pages
        };

        if (version is not null)
        {
            payload["language"] = version.LanguageCode;
            payload["versionTitle"] = version.VersionTitle;
        }

        if (firstPage.ValueKind == JsonValueKind.Object)
        {
            CopyJsonString(firstPage, payload, "heIndexTitle");
            CopyJsonString(firstPage, payload, "heBook");
            CopyJsonString(firstPage, payload, "heVersionTitle");
            CopyJsonString(firstPage, payload, "versionTitle");
            if (firstPage.TryGetProperty("categories", out var categories) &&
                categories.ValueKind == JsonValueKind.Array)
            {
                payload["categories"] = categories;
            }
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void CopyJsonString(JsonElement source, Dictionary<string, object?> target, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            target[propertyName] = value.GetString();
        }
    }

    private static async Task<string> DownloadTextWithProgressAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var url = BuildDownloadUrl(title, version);
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalLength = response.Content.Headers.ContentLength ?? -1L;
        if (totalLength <= 0)
        {
            progress.Report(0);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            progress.Report(100);
            return content;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        long totalRead = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
            builder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            progress.Report((double)totalRead / totalLength * 100);
        }

        progress.Report(100);
        return builder.ToString();
    }

    private static string BuildDownloadUrl(string title, SefariaVersionOption? version)
    {
        if (version is null ||
            string.IsNullOrWhiteSpace(version.VersionTitle) ||
            string.IsNullOrWhiteSpace(version.LanguageCode))
        {
            return $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(title)}.json";
        }

        var segment = $"{title} - {version.LanguageCode} - {version.VersionTitle}.json";
        return $"https://www.sefaria.org/download/version/{Uri.EscapeDataString(segment)}";
    }

    private string GetBookFilePath(string title, SefariaVersionOption? version)
    {
        var safeTitle = GetSafeFileName(title);
        var versionSegment = version is null
            ? "default"
            : GetSafeFileName($"{version.LanguageCode}_{version.VersionTitle}");

        return Path.Combine(DataFolder, $"{safeTitle}__{versionSegment}.json");
    }

    private string GetLegacyBookFilePath(string title)
    {
        return Path.Combine(DataFolder, $"{GetSafeFileName(title)}.json");
    }

    private static string GetSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeLanguageCode(string? languageCode, string? languageFamilyName)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            return languageCode.Trim().ToLowerInvariant();
        }

        var family = languageFamilyName?.Trim().ToLowerInvariant();
        return family switch
        {
            "english" => "en",
            "hebrew" => "he",
            _ => string.IsNullOrWhiteSpace(family) ? "en" : family.Length >= 2 ? family[..2] : family
        };
    }

    private string GetVersionProbeRef(string title)
    {
        return IsTalmudTitle(title) ? $"{title} 2a" : $"{title} 1";
    }

    private bool IsTalmudTitle(string title)
    {
        var orderLookup = BuildIndexOrderLookup();
        return orderLookup.TryGetValue(title, out var order) &&
            order.Categories.Any(category => string.Equals(category, "Talmud", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "Stendr/1.0");
        return client;
    }

    private static string ResolveProjectFolder()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Stendr.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Stendr");
    }

    private static void AddLibraryNode(SefariaIndexJsonNode node, SefariaCategoryNode parent)
    {
        var isCategory = node.Contents is { Count: > 0 };

        if (isCategory)
        {
            var category = new SefariaCategoryNode
            {
                Order = node.Order,
                EnDesc = node.EnDesc,
                HeDesc = node.HeDesc,
                EnShortDesc = node.EnShortDesc,
                HeShortDesc = node.HeShortDesc,
                HebrewCategory = node.HebrewCategory,
                Category = string.IsNullOrWhiteSpace(node.Category) ? node.Title : node.Category
            };

            parent.Contents.Add(category);

            foreach (var child in node.Contents!)
            {
                AddLibraryNode(child, category);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Title))
        {
            parent.Contents.Add(new SefariaBookNode
            {
                EnShortDesc = node.EnShortDesc,
                HeShortDesc = node.HeShortDesc,
                Order = node.Order,
                Categories = node.Categories ?? new List<string>(),
                PrimaryCategory = node.PrimaryCategory,
                HebrewTitle = node.HebrewTitle,
                Title = node.Title,
                Corpus = node.Corpus
            });
        }
    }

    private Dictionary<string, InstalledIndexOrder> BuildIndexOrderLookup()
    {
        try
        {
            var indexText = ReadJsonTextFile(IndexFilePath);
            var indexNodes = JsonSerializer.Deserialize<List<SefariaIndexJsonNode>>(indexText) ?? new List<SefariaIndexJsonNode>();
            var lookup = new Dictionary<string, InstalledIndexOrder>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in indexNodes)
            {
                AddIndexOrder(node, new List<string>(), new List<string?>(), new List<float>(), lookup);
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

        book.Categories = order.Categories;
        book.HebrewCategories = order.HebrewCategories;
        book.CategoryOrders = order.CategoryOrders;
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

    private List<InstalledSefariaBook> LoadInstalledBooks()
    {
        if (!File.Exists(InstalledBooksFilePath))
        {
            return new List<InstalledSefariaBook>();
        }

        try
        {
            var json = ReadJsonTextFile(InstalledBooksFilePath);
            return JsonSerializer.Deserialize<List<InstalledSefariaBook>>(json) ?? new List<InstalledSefariaBook>();
        }
        catch
        {
            return new List<InstalledSefariaBook>();
        }
    }

    private void SaveInstalledBooks(List<InstalledSefariaBook> installed)
    {
        var json = JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(InstalledBooksFilePath, json, Encoding.UTF8);
    }

    private void UpsertInstalledBook(InstalledSefariaBook book)
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

    private void RemoveInstalledBook(string title, SefariaVersionOption? version)
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

        ApplyJsonMetadata(record, json);
        return record;
    }

    private InstalledSefariaBook? TryCreateInstalledBookFromFile(string filePath)
    {
        try
        {
            return TryCreateInstalledBookFromJson(ReadJsonTextFile(filePath), filePath);
        }
        catch
        {
            return null;
        }
    }

    private static InstalledSefariaBook? TryCreateInstalledBookFromJson(string json, string filePath)
    {
        if (!IsDownloadedBookJson(json))
        {
            return null;
        }

        var record = new InstalledSefariaBook
        {
            FilePath = filePath
        };
        ApplyFileNameMetadata(record, filePath);
        ApplyJsonMetadata(record, json);
        return string.IsNullOrWhiteSpace(record.Title) ? null : record;
    }

    private static bool IsInstalledBookFile(string filePath)
    {
        try
        {
            return TryCreateInstalledBookFromJson(ReadJsonTextFile(filePath), filePath) is not null;
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
            var root = document.RootElement;
            var hasText = root.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.Array;
            var hasPages = root.TryGetProperty("pages", out var pages) &&
                pages.ValueKind == JsonValueKind.Array &&
                pages.GetArrayLength() > 0;

            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("title", out var title) &&
                !string.IsNullOrWhiteSpace(title.GetString()) &&
                (hasText || hasPages);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyJsonMetadata(InstalledSefariaBook record, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
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
            record.LanguageCode = language.GetString() ?? record.LanguageCode;
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

    private static void AppendTextElement(JsonElement element, List<string> lines, int chapterNumber)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(text);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var containsNestedArrays = element.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array);
        if (!containsNestedArrays)
        {
            var segmentNumber = 1;
            foreach (var item in element.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add($"{chapterNumber}:{segmentNumber}  {text}");
                }

                segmentNumber++;
            }

            return;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            AppendTextElement(item, lines, index);
            index++;
        }
    }

    private static void AppendTextUnits(JsonElement element, List<ReaderTextUnit> units, List<int> path)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                units.Add(new ReaderTextUnit(string.Join(".", path), text));
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 1;
        foreach (var item in element.EnumerateArray())
        {
            var nextPath = new List<int>(path) { index };
            AppendTextUnits(item, units, nextPath);
            index++;
        }
    }

    private static void AppendMishnahTextUnits(JsonElement textElement, List<ReaderTextUnit> units)
    {
        if (textElement.ValueKind != JsonValueKind.Array)
        {
            AppendTextUnits(textElement, units, new List<int>());
            return;
        }

        var chapterNumber = 1;
        foreach (var chapter in textElement.EnumerateArray())
        {
            if (chapter.ValueKind != JsonValueKind.Array)
            {
                var chapterText = NormalizeMishnahUnitText(CollectText(chapter));
                if (!string.IsNullOrWhiteSpace(chapterText))
                {
                    units.Add(new ReaderTextUnit(chapterNumber.ToString(), chapterText));
                }

                chapterNumber++;
                continue;
            }

            var mishnahNumber = 1;
            foreach (var mishnah in chapter.EnumerateArray())
            {
                var mishnahText = NormalizeMishnahUnitText(CollectText(mishnah));
                if (!string.IsNullOrWhiteSpace(mishnahText))
                {
                    units.Add(new ReaderTextUnit($"{chapterNumber}.{mishnahNumber}", mishnahText));
                }

                mishnahNumber++;
            }

            chapterNumber++;
        }
    }

    private static string CollectText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var text = CollectText(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(" ", parts);
    }

    private static string NormalizeMishnahUnitText(string text)
    {
        return CollapseWhitespace(RemoveSmallTagsWithContent(text));
    }

    private static string RemoveSmallTagsWithContent(string text)
    {
        var builder = new StringBuilder();
        var position = 0;
        while (position < text.Length)
        {
            var smallStart = text.IndexOf("<small", position, StringComparison.OrdinalIgnoreCase);
            if (smallStart < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, smallStart - position);
            var smallEnd = text.IndexOf("</small>", smallStart, StringComparison.OrdinalIgnoreCase);
            if (smallEnd < 0)
            {
                var openingEnd = text.IndexOf('>', smallStart);
                position = openingEnd < 0 ? text.Length : openingEnd + 1;
            }
            else
            {
                position = smallEnd + "</small>".Length;
            }
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static string ReadJsonTextFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static List<ReaderTextUnit> ReadTalmudTextUnits(InstalledSefariaBook book, JsonElement root)
    {
        var units = new List<ReaderTextUnit>();
        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var chapterTitle = string.Empty;
            var hebrewChapterTitle = string.Empty;
            foreach (var pageRoot in pages.EnumerateArray())
            {
                var pageChapterTitles = GetTalmudChapterTitles(pageRoot);
                if (!string.IsNullOrWhiteSpace(pageChapterTitles.ChapterTitle) ||
                    !string.IsNullOrWhiteSpace(pageChapterTitles.HebrewChapterTitle))
                {
                    chapterTitle = pageChapterTitles.ChapterTitle;
                    hebrewChapterTitle = pageChapterTitles.HebrewChapterTitle;
                }

                AppendTalmudPageTextUnits(book, pageRoot, units, chapterTitle, hebrewChapterTitle);
            }

            return units;
        }

        if (root.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.Array &&
            textElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Array))
        {
            AppendDirectTalmudTextUnits(textElement, units);
            return units;
        }

        var (singlePageChapterTitle, singlePageHebrewChapterTitle) = GetTalmudChapterTitles(root);
        AppendTalmudPageTextUnits(book, root, units, singlePageChapterTitle, singlePageHebrewChapterTitle);
        return units;
    }

    private static void AppendDirectTalmudTextUnits(JsonElement textElement, List<ReaderTextUnit> units)
    {
        var address = 0;
        foreach (var pageElement in textElement.EnumerateArray())
        {
            var page = FormatTalmudPageFromAddress(address);
            if (pageElement.ValueKind == JsonValueKind.Array)
            {
                var paragraphNumber = 1;
                foreach (var paragraph in pageElement.EnumerateArray())
                {
                    var paragraphText = CollapseWhitespace(CollectText(paragraph));
                    if (!string.IsNullOrWhiteSpace(paragraphText))
                    {
                        units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText));
                        paragraphNumber++;
                    }
                }
            }
            else
            {
                var text = CollapseWhitespace(CollectText(pageElement));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    units.Add(new ReaderTextUnit($"{page}.1", text));
                }
            }

            address++;
        }
    }

    private static string FormatTalmudPageFromAddress(int address)
    {
        var daf = address / 2 + 1;
        var side = address % 2 == 0 ? "a" : "b";
        return $"{daf}{side}";
    }

    private static void AppendTalmudPageTextUnits(
        InstalledSefariaBook book,
        JsonElement root,
        List<ReaderTextUnit> units,
        string chapterTitle,
        string hebrewChapterTitle)
    {
        var page = GetTalmudPage(root);
        var textPropertyName = IsHebrew(book) && root.TryGetProperty("he", out var heElement) && heElement.ValueKind == JsonValueKind.Array
            ? "he"
            : "text";

        if (!root.TryGetProperty(textPropertyName, out var textElement))
        {
            return;
        }

        if (textElement.ValueKind == JsonValueKind.Array)
        {
            var paragraphNumber = 1;
            foreach (var paragraph in textElement.EnumerateArray())
            {
                var paragraphText = CollapseWhitespace(CollectText(paragraph));
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    units.Add(new ReaderTextUnit($"{page}.{paragraphNumber}", paragraphText, chapterTitle, hebrewChapterTitle));
                    paragraphNumber++;
                }
            }
        }
        else
        {
            var text = CollapseWhitespace(CollectText(textElement));
            if (!string.IsNullOrWhiteSpace(text))
            {
                units.Add(new ReaderTextUnit($"{page}.1", text, chapterTitle, hebrewChapterTitle));
            }
        }
    }

    private static (string ChapterTitle, string HebrewChapterTitle) GetTalmudChapterTitles(JsonElement root)
    {
        if (!root.TryGetProperty("alts", out var alts) ||
            alts.ValueKind != JsonValueKind.Array ||
            alts.GetArrayLength() == 0)
        {
            return (string.Empty, string.Empty);
        }

        foreach (var alt in alts.EnumerateArray())
        {
            if (alt.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var chapterTitle = GetFirstAltTitle(alt, "en");
            var hebrewChapterTitle = GetFirstAltTitle(alt, "he");
            if (!string.IsNullOrWhiteSpace(chapterTitle) || !string.IsNullOrWhiteSpace(hebrewChapterTitle))
            {
                return (chapterTitle, hebrewChapterTitle);
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string GetFirstAltTitle(JsonElement alt, string propertyName)
    {
        if (!alt.TryGetProperty(propertyName, out var titles) ||
            titles.ValueKind != JsonValueKind.Array ||
            titles.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return titles[0].GetString() ?? string.Empty;
    }

    private static string GetTalmudPage(JsonElement root)
    {
        if (root.TryGetProperty("sections", out var sections) &&
            sections.ValueKind == JsonValueKind.Array &&
            sections.GetArrayLength() > 0)
        {
            var page = GetJsonScalarText(sections[0]);
            if (!string.IsNullOrWhiteSpace(page))
            {
                return page;
            }
        }

        if (root.TryGetProperty("sectionRef", out var sectionRef))
        {
            var reference = sectionRef.GetString();
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var parts = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return parts.Length == 0 ? "1" : parts[^1];
            }
        }

        return "1";
    }

    private static string? GetJsonScalarText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }
}
