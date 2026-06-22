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
    private const string SourcesFolderName = "sources";
    private const string DatabaseFolderName = "database";
    private const string IndexFileName = "sefaria_toc.json";
    private const string BooksManifestFileName = "sefaria_books.json";
    private const string InstalledBooksFileName = "installed-books.json";
    private const string VersionMetadataFileName = "version-metadata.db";
    private const string CommentariesDbFileName = "commentaries.db";
    private const string LinksDbFileName = "links.db";
    private const string ExportTableOfContentsUrl = "https://storage.googleapis.com/sefaria-export/table_of_contents.json";
    private const string ExportBooksManifestUrl = "https://raw.githubusercontent.com/Sefaria/Sefaria-Export/master/books.json";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim CommentaryCacheGate = new(1, 1);
    private static readonly SemaphoreSlim LinksCacheGate = new(1, 1);
    private readonly object _installedBooksCacheGate = new();
    private readonly SemaphoreSlim _booksManifestGate = new(1, 1);
    private List<InstalledSefariaBook>? _installedBooksCache;
    private Dictionary<string, List<SefariaVersionOption>>? _booksManifestCache;

    public SefariaLibraryService()
        : this(null)
    {
    }

    public SefariaLibraryService(string? storageRootFolder)
    {
        ProjectFolder = ResolveProjectFolder();
        SetStorageRootFolder(storageRootFolder);
    }

    public string ProjectFolder { get; }
    public string StorageRootFolder { get; private set; } = string.Empty;
    public string SourcesFolder { get; private set; } = string.Empty;
    public string DatabaseFolder { get; private set; } = string.Empty;
    public bool IsConfigured { get; private set; }
    public string IndexFilePath { get; private set; } = string.Empty;
    public string BooksManifestFilePath { get; private set; } = string.Empty;
    public string InstalledBooksFilePath { get; private set; } = string.Empty;
    public string VersionMetadataDbPath { get; private set; } = string.Empty;
    public string CommentariesDbPath { get; private set; } = string.Empty;
    public string LinksDbPath { get; private set; } = string.Empty;

    public void SetStorageRootFolder(string? storageRootFolder)
    {
        _booksManifestCache = null;
        lock (_installedBooksCacheGate)
        {
            _installedBooksCache = null;
        }

        if (string.IsNullOrWhiteSpace(storageRootFolder))
        {
            IsConfigured = false;
            StorageRootFolder = string.Empty;
            SourcesFolder = string.Empty;
            DatabaseFolder = string.Empty;
            IndexFilePath = string.Empty;
            BooksManifestFilePath = string.Empty;
            InstalledBooksFilePath = string.Empty;
            VersionMetadataDbPath = string.Empty;
            CommentariesDbPath = string.Empty;
            LinksDbPath = string.Empty;
            return;
        }

        IsConfigured = true;
        StorageRootFolder = Path.GetFullPath(storageRootFolder.Trim());
        SourcesFolder = Path.Combine(StorageRootFolder, SourcesFolderName);
        DatabaseFolder = Path.Combine(StorageRootFolder, DatabaseFolderName);
        IndexFilePath = Path.Combine(SourcesFolder, IndexFileName);
        BooksManifestFilePath = Path.Combine(SourcesFolder, BooksManifestFileName);
        InstalledBooksFilePath = Path.Combine(DatabaseFolder, InstalledBooksFileName);
        VersionMetadataDbPath = Path.Combine(DatabaseFolder, VersionMetadataFileName);
        CommentariesDbPath = Path.Combine(DatabaseFolder, CommentariesDbFileName);
        LinksDbPath = Path.Combine(DatabaseFolder, LinksDbFileName);
        Directory.CreateDirectory(StorageRootFolder);
        Directory.CreateDirectory(SourcesFolder);
        Directory.CreateDirectory(DatabaseFolder);
    }

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

        var request = new HttpRequestMessage(HttpMethod.Get, ExportTableOfContentsUrl);
        request.Headers.Add("accept", "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var indexData = await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(IndexFilePath, indexData, Encoding.UTF8, cancellationToken);
        return indexData;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "Stndr/1.0");
        return client;
    }

    private static string ResolveProjectFolder()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Stndr.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "Stndr");
    }

    private async Task<string> EnsureBooksManifestAvailableAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(BooksManifestFilePath))
        {
            var content = await File.ReadAllTextAsync(BooksManifestFilePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Get, ExportBooksManifestUrl);
        request.Headers.Add("accept", "application/json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var manifestData = await response.Content.ReadAsStringAsync(cancellationToken);
        await File.WriteAllTextAsync(BooksManifestFilePath, manifestData, Encoding.UTF8, cancellationToken);
        return manifestData;
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
}
