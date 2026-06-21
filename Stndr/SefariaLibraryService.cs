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
    private const string SefariaFolderName = "Sefaria";
    private const string IndexFileName = "sefaria_index.json";
    private const string InstalledBooksFileName = "installed-books.json";
    private const string VersionMetadataFileName = "version-metadata.db";
    private const string CommentariesDbFileName = "commentaries.db";
    private const string LinksDbFileName = "links.db";

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim CommentaryCacheGate = new(1, 1);
    private static readonly SemaphoreSlim LinksCacheGate = new(1, 1);
    private readonly object _installedBooksCacheGate = new();
    private List<InstalledSefariaBook>? _installedBooksCache;

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
    public string DataFolder { get; private set; } = string.Empty;
    public string IndexFilePath { get; private set; } = string.Empty;
    public string InstalledBooksFilePath { get; private set; } = string.Empty;
    public string VersionMetadataDbPath { get; private set; } = string.Empty;
    public string CommentariesDbPath { get; private set; } = string.Empty;
    public string LinksDbPath { get; private set; } = string.Empty;

    public void SetStorageRootFolder(string? storageRootFolder)
    {
        StorageRootFolder = AppSettingsService.NormalizeDataStorageFolder(storageRootFolder);
        DataFolder = Path.Combine(StorageRootFolder, SefariaFolderName);
        IndexFilePath = Path.Combine(DataFolder, IndexFileName);
        InstalledBooksFilePath = Path.Combine(DataFolder, InstalledBooksFileName);
        VersionMetadataDbPath = Path.Combine(DataFolder, VersionMetadataFileName);
        CommentariesDbPath = Path.Combine(DataFolder, CommentariesDbFileName);
        LinksDbPath = Path.Combine(DataFolder, LinksDbFileName);
        Directory.CreateDirectory(StorageRootFolder);
        Directory.CreateDirectory(DataFolder);
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

        var request = new HttpRequestMessage(HttpMethod.Get, "https://www.sefaria.org/api/index/");
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
