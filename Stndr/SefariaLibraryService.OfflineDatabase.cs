using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private readonly object _offlineCacheGate = new();
    private List<InstalledSefariaBook>? _offlineBooksCache;
    private DateTime _offlineDatabaseLastWriteUtc;
    private long _offlineDatabaseLength;
    private readonly Dictionary<string, BookSchema?> _offlineSchemaCache = new(StringComparer.Ordinal);

    public string OfflineLibraryDatabasePath => IsConfigured
        ? SefariaOfflineLibraryPaths.ActiveDatabase(StorageRootFolder)
        : string.Empty;

    public bool HasOfflineLibrary => !string.IsNullOrWhiteSpace(OfflineLibraryDatabasePath) &&
        File.Exists(OfflineLibraryDatabasePath);

    /// <summary>
    /// Call after the active offline database file has been replaced. Releases pooled SQLite
    /// handles and drops in-memory offline caches. Open reader content is left as-is.
    /// </summary>
    public void NotifyOfflineLibraryReplaced()
    {
        SqliteConnection.ClearAllPools();
        InvalidateOfflineLibraryCaches();
    }

    private void InvalidateOfflineLibraryCaches()
    {
        lock (_offlineCacheGate)
        {
            _offlineBooksCache = null;
            _offlineDatabaseLastWriteUtc = default;
            _offlineDatabaseLength = 0;
            _offlineSchemaCache.Clear();
        }
    }

    private List<InstalledSefariaBook> GetOfflineLibraryBooks()
    {
        if (!HasOfflineLibrary) return new();
        var info = new FileInfo(OfflineLibraryDatabasePath);
        lock (_offlineCacheGate)
        {
            if (_offlineBooksCache is not null && _offlineDatabaseLastWriteUtc == info.LastWriteTimeUtc &&
                _offlineDatabaseLength == info.Length)
                return CloneInstalledBooks(_offlineBooksCache);
        }

        var books = new List<InstalledSefariaBook>(12_000);
        using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id,w.title,w.he_title,w.categories_json,v.actual_language,v.language,
                   v.version_title,v.compressed_bytes,v.license,v.version_source,
                   v.is_primary,v.is_source,v.priority,v.segment_count
            FROM versions v JOIN works w ON w.id=v.work_id
            WHERE v.segment_count>0
            ORDER BY w.id,v.is_primary DESC,v.is_source DESC,v.priority DESC,v.id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var language = FirstNonEmpty(reader.GetString(4), reader.GetString(5), "en");
            books.Add(new InstalledSefariaBook
            {
                OfflineVersionId = id,
                Title = reader.GetString(1),
                HebrewTitle = reader.GetString(2),
                Categories = ParseStringArray(reader.GetString(3)),
                LanguageCode = NormalizeOfflineLanguageCode(language),
                VersionTitle = reader.GetString(6),
                FilePath = $"sefaria-library://version/{id}",
                FileLength = reader.GetInt64(7),
                FileLastWriteTimeUtc = info.LastWriteTimeUtc,
                License = reader.GetString(8),
                VersionSource = reader.GetString(9),
                OfflineIsPrimary = reader.GetInt64(10) != 0,
                OfflineIsSource = reader.GetInt64(11) != 0,
                OfflinePriority = reader.GetDouble(12),
                SegmentCount = reader.GetInt64(13)
            });
        }

        lock (_offlineCacheGate)
        {
            _offlineBooksCache = CloneInstalledBooks(books);
            _offlineDatabaseLastWriteUtc = info.LastWriteTimeUtc;
            _offlineDatabaseLength = info.Length;
        }
        return books;
    }

    private string ReadOfflineVersionJson(long versionId)
    {
        using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_zlib FROM versions WHERE id=$id";
        command.Parameters.AddWithValue("$id", versionId);
        var compressed = command.ExecuteScalar() as byte[] ??
            throw new InvalidDataException($"Offline version {versionId} was not found.");
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        var chapterJson = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        return $"{{\"text\":{chapterJson}}}";
    }

    private string ReadBookJson(InstalledSefariaBook book) => book.IsOfflineLibraryVersion
        ? ReadOfflineVersionJson(book.OfflineVersionId)
        : ReadJsonTextFile(book.FilePath);

    private BookSchema? GetOfflineBookSchema(string title)
    {
        if (!HasOfflineLibrary) return null;
        lock (_offlineCacheGate)
            if (_offlineSchemaCache.TryGetValue(title, out var cached)) return cached;

        using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT he_title,schema_json,alt_structs_json FROM works WHERE title=$title";
        command.Parameters.AddWithValue("$title", title);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var schema = ParseOfflineSchema(title, reader.GetString(0), reader.GetString(1), reader.GetString(2));
        lock (_offlineCacheGate) _offlineSchemaCache[title] = schema;
        return schema;
    }

    private static BookSchema? ParseOfflineSchema(string title, string heTitle, string schemaJson, string altsJson)
    {
        try
        {
            using var schemaDocument = JsonDocument.Parse(schemaJson);
            var root = schemaDocument.RootElement;
            var result = new BookSchema { Title = title, HeTitle = heTitle };
            if (root.TryGetProperty("depth", out var depth) && depth.TryGetInt32(out var depthValue)) result.Depth = depthValue;
            if (root.TryGetProperty("sectionNames", out var sections) && sections.ValueKind == JsonValueKind.Array)
                result.SectionNames.AddRange(sections.EnumerateArray().Select(item => item.GetString() ?? ""));
            if (root.TryGetProperty("heSectionNames", out var heSections) && heSections.ValueKind == JsonValueKind.Array)
                result.HeSectionNames.AddRange(heSections.EnumerateArray().Select(item => item.GetString() ?? ""));

            using var altsDocument = JsonDocument.Parse(altsJson);
            if (altsDocument.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var structure in altsDocument.RootElement.EnumerateObject())
            {
                var nodes = new List<SchemaAltNode>();
                if (structure.Value.TryGetProperty("nodes", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array)
                foreach (var node in nodeArray.EnumerateArray())
                    nodes.Add(new SchemaAltNode
                    {
                        Title = GetPrimaryNodeTitle(node, "en"),
                        HeTitle = GetPrimaryNodeTitle(node, "he"),
                        WholeRef = node.TryGetProperty("wholeRef", out var wholeRef) ? wholeRef.GetString() ?? "" : "",
                        NumericEquivalent = node.TryGetProperty("numeric_equivalent", out var numeric) && numeric.TryGetInt32(out var number) ? number : 0
                    });
                result.AltStructures[structure.Name] = nodes;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private static string GetPrimaryNodeTitle(JsonElement node, string language)
    {
        if (!node.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) return "";
        string fallback = "";
        foreach (var item in titles.EnumerateArray())
        {
            if (!item.TryGetProperty("lang", out var lang) || lang.GetString() != language) continue;
            var text = item.TryGetProperty("text", out var title) ? title.GetString() ?? "" : "";
            fallback = string.IsNullOrWhiteSpace(fallback) ? text : fallback;
            if (item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True) return text;
        }
        return fallback;
    }

    private List<SefariaVersionOption> GetOfflineVersionOptions(string title) =>
        GetOfflineLibraryBooks().Where(book => string.Equals(book.Title, title, StringComparison.Ordinal))
            .Select(book => new SefariaVersionOption
            {
                LanguageCode = book.LanguageCode,
                LanguageFamilyName = IsHebrew(book) ? "hebrew" : "english",
                VersionTitle = book.VersionTitle,
                DisplayText = $"{book.VersionTitle} ({book.LanguageCode})",
                DownloadUrl = book.FilePath
            }).ToList();

    private SefariaCategoryNode BuildOfflineLibraryTree(CancellationToken token)
    {
        var root = new SefariaCategoryNode { Category = "Sefaria" };
        foreach (var group in GetOfflineLibraryBooks().GroupBy(book => book.Title, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var representative = group.First();
            var parent = root;
            foreach (var categoryName in representative.Categories.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var category = parent.Contents.OfType<SefariaCategoryNode>()
                    .FirstOrDefault(item => string.Equals(item.Category, categoryName, StringComparison.OrdinalIgnoreCase));
                if (category is null)
                {
                    category = new SefariaCategoryNode { Category = categoryName };
                    parent.Contents.Add(category);
                }
                parent = category;
            }

            var versions = group.Select(book => new SefariaVersionOption
            {
                LanguageCode = book.LanguageCode,
                LanguageFamilyName = IsHebrew(book) ? "hebrew" : "english",
                VersionTitle = book.VersionTitle,
                DisplayText = $"{book.VersionTitle} ({book.LanguageCode})",
                DownloadUrl = book.FilePath
            }).ToList();
            parent.Contents.Add(new SefariaBookNode
            {
                Title = representative.Title,
                HebrewTitle = representative.HebrewTitle,
                Categories = new List<string>(representative.Categories),
                PrimaryCategory = representative.Categories.FirstOrDefault(),
                Versions = versions,
                SelectedVersion = versions.FirstOrDefault(),
                IsVersionsLoaded = true,
                IsDownloaded = true
            });
        }
        SortOfflineLibraryCategories(root);
        return root;
    }

    private Dictionary<string, WorkShortDescription> LoadOfflineWorkDescriptions()
    {
        var result = new Dictionary<string, WorkShortDescription>(StringComparer.OrdinalIgnoreCase);
        if (!HasOfflineLibrary) return result;
        using var connection = OpenOfflineConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT title,en_short_description,he_short_description,en_description,he_description FROM works";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var english = FirstNonEmpty(reader.GetString(1), reader.GetString(3));
            var hebrew = FirstNonEmpty(reader.GetString(2), reader.GetString(4));
            if (!string.IsNullOrWhiteSpace(english) || !string.IsNullOrWhiteSpace(hebrew))
                result[reader.GetString(0)] = new WorkShortDescription(english, hebrew);
        }
        return result;
    }

    private static void SortOfflineLibraryCategories(SefariaCategoryNode category)
    {
        foreach (var child in category.Contents.OfType<SefariaCategoryNode>()) SortOfflineLibraryCategories(child);
        category.Contents = new System.Collections.ObjectModel.ObservableCollection<SefariaNode>(category.Contents
            .OrderBy(node => node is SefariaCategoryNode ? 0 : 1)
            .ThenBy(node => node switch
            {
                SefariaCategoryNode child => child.Category,
                SefariaBookNode book => book.Title,
                _ => ""
            }, StringComparer.OrdinalIgnoreCase));
    }

    private SqliteConnection OpenOfflineConnection()
    {
        var connection = new SqliteConnection($"Data Source={OfflineLibraryDatabasePath};Mode=ReadOnly;Pooling=True");
        connection.Open();
        return connection;
    }

    private static List<string> ParseStringArray(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static string NormalizeOfflineLanguageCode(string language)
    {
        var normalized = language.Trim().ToLowerInvariant();
        return normalized switch { "hebrew" => "he", "english" => "en", _ when normalized.Length > 0 => normalized, _ => "en" };
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
