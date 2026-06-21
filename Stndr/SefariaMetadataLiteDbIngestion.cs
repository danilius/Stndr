using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Formats.Tar;
using LiteDB;
using LiteDatabase = LiteDB.LiteDatabase;
using MongoBsonArray = MongoDB.Bson.BsonArray;
using MongoBsonDocument = MongoDB.Bson.BsonDocument;
using MongoBsonSerializer = MongoDB.Bson.Serialization.BsonSerializer;

namespace Stndr;

public static class SefariaMetadataLiteDbIngestionCommand
{
    private const string IngestArgument = "--ingest-sefaria-metadata";
    private const string DumpArgument = "--dump";
    private const string OutArgument = "--out";
    private const string BatchArgument = "--batch-size";

    public static bool TryRun(string[] args, out int exitCode)
    {
        if (!args.Any(arg => string.Equals(arg, IngestArgument, StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = 0;
            return false;
        }

        try
        {
            var options = ParseOptions(args);
            ValidateOptions(options);

            var dumpPath = Path.GetFullPath(options[DumpArgument]);
            var outputDbPath = ResolveOutputDbPath(options);
            var batchSize = ResolveBatchSize(options);

            var ingestor = new SefariaMetadataLiteDbIngestor(outputDbPath, batchSize);
            var summary = ingestor.IngestFromTarGz(dumpPath);

            Console.WriteLine($"Ingestion complete: {outputDbPath}");
            Console.WriteLine($"works={summary.Works}, versions={summary.Versions}, categories={summary.Categories}, terms={summary.Terms}, link_edges={summary.LinkEdges}, topic_links={summary.TopicLinks}");
            Console.WriteLine("Keying contract: text_version_key = <workKey>|<language>|<versionTitle>, json_file_name = <safeWorkKey>__<safeLanguage_versionTitle>.json");
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Metadata ingestion failed: {ex.Message}");
            exitCode = 1;
        }

        return true;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? waitingForValue = null;
        foreach (var rawArg in args)
        {
            if (string.IsNullOrWhiteSpace(rawArg))
            {
                continue;
            }

            var arg = rawArg.Trim();
            if (waitingForValue is not null)
            {
                options[waitingForValue] = arg;
                waitingForValue = null;
                continue;
            }

            if (string.Equals(arg, IngestArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                waitingForValue = arg;
            }
        }

        if (waitingForValue is not null)
        {
            throw new ArgumentException($"Missing value for {waitingForValue}.");
        }

        return options;
    }

    private static void ValidateOptions(IReadOnlyDictionary<string, string> options)
    {
        if (!options.ContainsKey(DumpArgument))
        {
            throw new ArgumentException("Missing required argument: --dump <path-to-sefaria-dump.tar.gz>.");
        }
    }

    private static string ResolveOutputDbPath(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue(OutArgument, out var outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            return Path.GetFullPath(outPath);
        }

        var dataFolder = AppSettingsService.NormalizeDataStorageFolder(null);
        var sefariaFolder = Path.Combine(dataFolder, "Sefaria");
        Directory.CreateDirectory(sefariaFolder);
        return Path.Combine(sefariaFolder, "metadata.db");
    }

    private static int ResolveBatchSize(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue(BatchArgument, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 5000;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException("--batch-size must be a positive integer.");
        }

        return parsed;
    }
}

public sealed class SefariaMetadataLiteDbIngestor
{
    private const string IndexBsonPath = "dump/sefaria/index.bson";
    private const string TextsBsonPath = "dump/sefaria/texts.bson";
    private const string CategoriesBsonPath = "dump/sefaria/category.bson";
    private const string TermsBsonPath = "dump/sefaria/term.bson";
    private const string LinksBsonPath = "dump/sefaria/links.bson";
    private const string TopicLinksBsonPath = "dump/sefaria/topic_links.bson";

    private readonly string _databasePath;
    private readonly int _batchSize;

    public SefariaMetadataLiteDbIngestor(string databasePath, int batchSize = 5000)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("databasePath must not be empty.", nameof(databasePath));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        _databasePath = Path.GetFullPath(databasePath);
        _batchSize = batchSize;
    }

    public SefariaMetadataIngestionRecord IngestFromTarGz(string tarGzPath)
    {
        if (string.IsNullOrWhiteSpace(tarGzPath))
        {
            throw new ArgumentException("tarGzPath must not be empty.", nameof(tarGzPath));
        }

        var resolvedPath = Path.GetFullPath(tarGzPath);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("Mongo dump archive not found.", resolvedPath);
        }

        var parentDirectory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        using var database = new LiteDatabase(_databasePath);
        EnsureIndexes(database);
        ClearCollections(database);
        var summary = IngestCollections(database, resolvedPath);
        UpsertIngestionRecord(database, resolvedPath, summary);
        return summary;
    }

    private SefariaMetadataIngestionRecord IngestCollections(LiteDatabase database, string dumpPath)
    {
        var workKeysByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var summary = new SefariaMetadataIngestionRecord();

        var works = database.GetCollection<SefariaWorkMetadata>("works");
        var versions = database.GetCollection<SefariaTextVersionMetadata>("versions");
        var categories = database.GetCollection<SefariaCategoryMetadata>("categories");
        var terms = database.GetCollection<SefariaTermMetadata>("terms");
        var titleLookup = database.GetCollection<SefariaTitleLookup>("title_lookup");
        var linkEdges = database.GetCollection<SefariaLinkEdgeMetadata>("link_edges");
        var topicLinks = database.GetCollection<SefariaTopicLinkMetadata>("topic_links");

        var workBatch = new List<SefariaWorkMetadata>(_batchSize);
        var versionBatch = new List<SefariaTextVersionMetadata>(_batchSize);
        var categoryBatch = new List<SefariaCategoryMetadata>(_batchSize);
        var termBatch = new List<SefariaTermMetadata>(_batchSize);
        var titleLookupBatch = new List<SefariaTitleLookup>(_batchSize);
        var linkBatch = new List<SefariaLinkEdgeMetadata>(_batchSize);
        var topicLinkBatch = new List<SefariaTopicLinkMetadata>(_batchSize);

        using var fileStream = File.OpenRead(dumpPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null || entry.EntryType != TarEntryType.RegularFile)
            {
                continue;
            }

            var normalizedName = NormalizeTarPath(entry.Name);
            if (string.Equals(normalizedName, IndexBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    var work = ConvertWork(doc);
                    workBatch.Add(work);
                    summary.Works++;
                    if (!string.IsNullOrWhiteSpace(work.Title))
                    {
                        workKeysByTitle[work.Title] = work.WorkKey;
                    }

                    foreach (var lookup in BuildWorkLookupRows(work))
                    {
                        titleLookupBatch.Add(lookup);
                    }

                    FlushIfNeeded(workBatch, works, _batchSize);
                    FlushIfNeeded(titleLookupBatch, titleLookup, _batchSize);
                }
            }
            else if (string.Equals(normalizedName, TextsBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    var version = ConvertVersion(doc, workKeysByTitle);
                    versionBatch.Add(version);
                    summary.Versions++;
                    foreach (var lookup in BuildVersionLookupRows(version))
                    {
                        titleLookupBatch.Add(lookup);
                    }

                    FlushIfNeeded(versionBatch, versions, _batchSize);
                    FlushIfNeeded(titleLookupBatch, titleLookup, _batchSize);
                }
            }
            else if (string.Equals(normalizedName, CategoriesBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    categoryBatch.Add(ConvertCategory(doc));
                    summary.Categories++;
                    FlushIfNeeded(categoryBatch, categories, _batchSize);
                }
            }
            else if (string.Equals(normalizedName, TermsBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    var term = ConvertTerm(doc);
                    termBatch.Add(term);
                    summary.Terms++;
                    foreach (var lookup in BuildTermLookupRows(term))
                    {
                        titleLookupBatch.Add(lookup);
                    }

                    FlushIfNeeded(termBatch, terms, _batchSize);
                    FlushIfNeeded(titleLookupBatch, titleLookup, _batchSize);
                }
            }
            else if (string.Equals(normalizedName, LinksBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    foreach (var link in ConvertLinkEdges(doc))
                    {
                        linkBatch.Add(link);
                        summary.LinkEdges++;
                    }

                    FlushIfNeeded(linkBatch, linkEdges, _batchSize);
                }
            }
            else if (string.Equals(normalizedName, TopicLinksBsonPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var doc in ReadBsonDocuments(entry.DataStream))
                {
                    topicLinkBatch.Add(ConvertTopicLink(doc));
                    summary.TopicLinks++;
                    FlushIfNeeded(topicLinkBatch, topicLinks, _batchSize);
                }
            }
        }

        FlushAll(workBatch, works);
        FlushAll(versionBatch, versions);
        FlushAll(categoryBatch, categories);
        FlushAll(termBatch, terms);
        FlushAll(titleLookupBatch, titleLookup);
        FlushAll(linkBatch, linkEdges);
        FlushAll(topicLinkBatch, topicLinks);

        return summary;
    }

    private static void EnsureIndexes(LiteDatabase database)
    {
        var works = database.GetCollection<SefariaWorkMetadata>("works");
        works.EnsureIndex(x => x.WorkKey, unique: true);
        works.EnsureIndex(x => x.Title);
        works.EnsureIndex(x => x.WorkKeyNormalized);
        works.EnsureIndex(x => x.PrimaryCategory);
        works.EnsureIndex(x => x.Categories);
        works.EnsureIndex(x => x.TitleVariants);

        var versions = database.GetCollection<SefariaTextVersionMetadata>("versions");
        versions.EnsureIndex(x => x.TextVersionKey, unique: true);
        versions.EnsureIndex(x => x.WorkKey);
        versions.EnsureIndex(x => x.WorkTitle);
        versions.EnsureIndex(x => x.LanguageCode);
        versions.EnsureIndex(x => x.VersionTitle);
        versions.EnsureIndex(x => x.JsonFileName);

        var categories = database.GetCollection<SefariaCategoryMetadata>("categories");
        categories.EnsureIndex(x => x.PathKey, unique: true);
        categories.EnsureIndex(x => x.Path);
        categories.EnsureIndex(x => x.LastPath);
        categories.EnsureIndex(x => x.Depth);

        var terms = database.GetCollection<SefariaTermMetadata>("terms");
        terms.EnsureIndex(x => x.TermKey, unique: true);
        terms.EnsureIndex(x => x.Name);
        terms.EnsureIndex(x => x.Category);
        terms.EnsureIndex(x => x.Titles);

        var titleLookup = database.GetCollection<SefariaTitleLookup>("title_lookup");
        titleLookup.EnsureIndex(x => x.Id, unique: true);
        titleLookup.EnsureIndex(x => x.NormalizedTitle);
        titleLookup.EnsureIndex(x => x.EntityType);
        titleLookup.EnsureIndex(x => x.EntityKey);
        titleLookup.EnsureIndex(x => x.WorkKey);

        var links = database.GetCollection<SefariaLinkEdgeMetadata>("link_edges");
        links.EnsureIndex(x => x.EdgeKey, unique: true);
        links.EnsureIndex(x => x.AnchorRef);
        links.EnsureIndex(x => x.AnchorExpandedRefs);
        links.EnsureIndex(x => x.TargetRef);
        links.EnsureIndex(x => x.LinkType);

        var topicLinks = database.GetCollection<SefariaTopicLinkMetadata>("topic_links");
        topicLinks.EnsureIndex(x => x.LinkKey, unique: true);
        topicLinks.EnsureIndex(x => x.TopicFrom);
        topicLinks.EnsureIndex(x => x.TopicTo);
        topicLinks.EnsureIndex(x => x.LinkClass);
        topicLinks.EnsureIndex(x => x.LinkType);
        topicLinks.EnsureIndex(x => x.ExpandedRefs);
    }

    private static void ClearCollections(LiteDatabase database)
    {
        database.DropCollection("works");
        database.DropCollection("versions");
        database.DropCollection("categories");
        database.DropCollection("terms");
        database.DropCollection("title_lookup");
        database.DropCollection("link_edges");
        database.DropCollection("topic_links");
        database.DropCollection("ingestion_info");
        EnsureIndexes(database);
    }

    private static void UpsertIngestionRecord(LiteDatabase database, string dumpPath, SefariaMetadataIngestionRecord summary)
    {
        var fileInfo = new FileInfo(dumpPath);
        summary.Id = "latest";
        summary.IngestedAtUtc = DateTime.UtcNow;
        summary.DumpFilePath = fileInfo.FullName;
        summary.DumpFileName = fileInfo.Name;
        summary.DumpFileBytes = fileInfo.Length;

        var collection = database.GetCollection<SefariaMetadataIngestionRecord>("ingestion_info");
        collection.Upsert(summary);
    }

    private static SefariaWorkMetadata ConvertWork(MongoBsonDocument doc)
    {
        var schema = GetDocument(doc, "schema");
        var title = GetString(doc, "title");
        var workKey = FirstNonEmpty(
            GetString(schema, "key"),
            title,
            GetString(doc, "_id"));

        var titleVariants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(titleVariants, title);
        AddIfPresent(titleVariants, GetString(doc, "heTitle"));
        AddIfPresent(titleVariants, GetPrimaryTitle(schema, "he"));
        AddIfPresent(titleVariants, workKey);

        foreach (var titleDoc in GetArray(schema, "titles")
                     .Where(item => item.IsBsonDocument)
                     .Select(item => item.AsBsonDocument))
        {
            AddIfPresent(titleVariants, GetString(titleDoc, "text"));
        }

        var categories = GetStringList(doc, "categories");
        var orderPath = GetArray(doc, "order")
            .Where(item => item.IsNumeric)
            .Select(item => item.ToDouble())
            .ToList();

        return new SefariaWorkMetadata
        {
            WorkKey = workKey,
            WorkKeyNormalized = NormalizeLookupToken(workKey),
            Title = title,
            HebrewTitle = FirstNonEmpty(GetString(doc, "heTitle"), GetPrimaryTitle(schema, "he")),
            Categories = categories,
            PrimaryCategory = categories.FirstOrDefault() ?? string.Empty,
            OrderPath = orderPath,
            SchemaDepth = GetInt(schema, "depth"),
            SectionNames = GetStringList(schema, "sectionNames"),
            AddressTypes = GetStringList(schema, "addressTypes"),
            Lengths = GetArray(schema, "lengths").Where(item => item.IsNumeric).Select(item => item.ToInt32()).ToList(),
            TitleVariants = titleVariants.ToList(),
            SourceMongoId = GetString(doc, "_id")
        };
    }

    private static SefariaTextVersionMetadata ConvertVersion(MongoBsonDocument doc, IReadOnlyDictionary<string, string> workKeysByTitle)
    {
        var title = GetString(doc, "title");
        var workKey = workKeysByTitle.TryGetValue(title, out var indexedWorkKey)
            ? indexedWorkKey
            : title;

        var languageCode = NormalizeLanguageCode(GetString(doc, "language"));
        var actualLanguage = NormalizeLanguageCode(GetString(doc, "actualLanguage"));
        var versionTitle = GetString(doc, "versionTitle");
        var textVersionKey = BuildTextVersionKey(workKey, languageCode, versionTitle);

        return new SefariaTextVersionMetadata
        {
            TextVersionKey = textVersionKey,
            WorkKey = workKey,
            WorkTitle = title,
            LanguageCode = languageCode,
            ActualLanguageCode = actualLanguage,
            VersionTitle = versionTitle,
            VersionSource = GetString(doc, "versionSource"),
            Priority = GetInt(doc, "priority"),
            DigitizedBySefaria = GetBool(doc, "digitizedBySefaria"),
            JsonFileName = BuildJsonFileName(workKey, languageCode, versionTitle),
            SourceMongoId = GetString(doc, "_id")
        };
    }

    private static SefariaCategoryMetadata ConvertCategory(MongoBsonDocument doc)
    {
        var path = GetStringList(doc, "path");
        var pathKey = path.Count == 0
            ? GetString(doc, "_id")
            : string.Join("/", path);

        return new SefariaCategoryMetadata
        {
            PathKey = pathKey,
            Path = path,
            LastPath = FirstNonEmpty(GetString(doc, "lastPath"), path.LastOrDefault() ?? string.Empty),
            Depth = GetInt(doc, "depth"),
            SharedTitle = GetString(doc, "sharedTitle"),
            EnglishDescription = FirstNonEmpty(GetString(doc, "enDesc"), GetString(doc, "enShortDesc")),
            HebrewDescription = FirstNonEmpty(GetString(doc, "heDesc"), GetString(doc, "heShortDesc")),
            Order = GetDouble(doc, "order"),
            SourceMongoId = GetString(doc, "_id")
        };
    }

    private static SefariaTermMetadata ConvertTerm(MongoBsonDocument doc)
    {
        var termKey = FirstNonEmpty(GetString(doc, "name"), GetString(doc, "_id"));
        var titleDocs = GetArray(doc, "titles")
            .Where(item => item.IsBsonDocument)
            .Select(item => item.AsBsonDocument)
            .ToList();

        return new SefariaTermMetadata
        {
            TermKey = termKey,
            Name = GetString(doc, "name"),
            Category = GetString(doc, "category"),
            Scheme = GetString(doc, "scheme"),
            Reference = GetString(doc, "ref"),
            Titles = titleDocs.Select(titleDoc => GetString(titleDoc, "text"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Languages = titleDocs.Select(titleDoc => GetString(titleDoc, "lang"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Order = GetInt(doc, "order"),
            SourceMongoId = GetString(doc, "_id")
        };
    }

    private static IEnumerable<SefariaLinkEdgeMetadata> ConvertLinkEdges(MongoBsonDocument doc)
    {
        var linkId = GetString(doc, "_id");
        var refs = GetStringList(doc, "refs");
        if (refs.Count < 2)
        {
            yield break;
        }

        var expandedRef0 = GetStringList(doc, "expandedRefs0");
        var expandedRef1 = GetStringList(doc, "expandedRefs1");

        yield return new SefariaLinkEdgeMetadata
        {
            EdgeKey = $"{linkId}:0",
            LinkId = linkId,
            AnchorRef = refs[0],
            TargetRef = refs[1],
            AnchorExpandedRefs = expandedRef0,
            TargetExpandedRefs = expandedRef1,
            LinkType = GetString(doc, "type"),
            AutoGenerated = GetBool(doc, "auto"),
            GeneratedBy = GetString(doc, "generated_by"),
            SourceTextOid = GetString(doc, "source_text_oid")
        };

        yield return new SefariaLinkEdgeMetadata
        {
            EdgeKey = $"{linkId}:1",
            LinkId = linkId,
            AnchorRef = refs[1],
            TargetRef = refs[0],
            AnchorExpandedRefs = expandedRef1,
            TargetExpandedRefs = expandedRef0,
            LinkType = GetString(doc, "type"),
            AutoGenerated = GetBool(doc, "auto"),
            GeneratedBy = GetString(doc, "generated_by"),
            SourceTextOid = GetString(doc, "source_text_oid")
        };
    }

    private static SefariaTopicLinkMetadata ConvertTopicLink(MongoBsonDocument doc)
    {
        var id = GetString(doc, "_id");
        var from = GetString(doc, "fromTopic");
        var to = GetString(doc, "toTopic");

        return new SefariaTopicLinkMetadata
        {
            LinkKey = FirstNonEmpty(id, $"{from}->{to}"),
            TopicFrom = from,
            TopicTo = to,
            LinkClass = GetString(doc, "class"),
            LinkType = GetString(doc, "linkType"),
            DataSource = GetString(doc, "dataSource"),
            ExpandedRefs = GetStringList(doc, "expandedRefs")
        };
    }

    private static IEnumerable<SefariaTitleLookup> BuildWorkLookupRows(SefariaWorkMetadata work)
    {
        foreach (var title in work.TitleVariants)
        {
            var normalized = NormalizeLookupToken(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            yield return new SefariaTitleLookup
            {
                Id = $"work:{work.WorkKey}:{normalized}",
                NormalizedTitle = normalized,
                Title = title,
                EntityType = "work",
                EntityKey = work.WorkKey,
                WorkKey = work.WorkKey
            };
        }
    }

    private static IEnumerable<SefariaTitleLookup> BuildVersionLookupRows(SefariaTextVersionMetadata version)
    {
        if (string.IsNullOrWhiteSpace(version.VersionTitle))
        {
            yield break;
        }

        var combined = $"{version.WorkTitle} | {version.VersionTitle} | {version.LanguageCode}";
        yield return new SefariaTitleLookup
        {
            Id = $"version:{version.TextVersionKey}",
            NormalizedTitle = NormalizeLookupToken(combined),
            Title = combined,
            EntityType = "version",
            EntityKey = version.TextVersionKey,
            WorkKey = version.WorkKey,
            Language = version.LanguageCode
        };
    }

    private static IEnumerable<SefariaTitleLookup> BuildTermLookupRows(SefariaTermMetadata term)
    {
        foreach (var title in term.Titles)
        {
            var normalized = NormalizeLookupToken(title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            yield return new SefariaTitleLookup
            {
                Id = $"term:{term.TermKey}:{normalized}",
                NormalizedTitle = normalized,
                Title = title,
                EntityType = "term",
                EntityKey = term.TermKey
            };
        }
    }

    private static void FlushIfNeeded<T>(List<T> batch, ILiteCollection<T> collection, int batchSize) where T : class
    {
        if (batch.Count < batchSize)
        {
            return;
        }

        collection.Upsert(batch);
        batch.Clear();
    }

    private static void FlushAll<T>(List<T> batch, ILiteCollection<T> collection) where T : class
    {
        if (batch.Count == 0)
        {
            return;
        }

        collection.Upsert(batch);
        batch.Clear();
    }

    private static IEnumerable<MongoBsonDocument> ReadBsonDocuments(Stream stream)
    {
        while (true)
        {
            var header = new byte[4];
            if (!TryReadExact(stream, header, 0, header.Length))
            {
                yield break;
            }

            var documentLength = BitConverter.ToInt32(header, 0);
            if (documentLength == 0)
            {
                yield break;
            }

            if (documentLength < 5)
            {
                throw new InvalidDataException($"Invalid BSON document length: {documentLength}.");
            }

            var documentBytes = new byte[documentLength];
            Buffer.BlockCopy(header, 0, documentBytes, 0, 4);
            if (!TryReadExact(stream, documentBytes, 4, documentLength - 4))
            {
                throw new EndOfStreamException("Unexpected end of stream while reading BSON document.");
            }

            yield return MongoBsonSerializer.Deserialize<MongoBsonDocument>(documentBytes);
        }
    }

    private static bool TryReadExact(Stream stream, byte[] buffer, int offset, int length)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = stream.Read(buffer, offset + totalRead, length - totalRead);
            if (read == 0)
            {
                return totalRead == 0;
            }

            totalRead += read;
        }

        return true;
    }

    private static string NormalizeTarPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string BuildTextVersionKey(string workKey, string languageCode, string versionTitle)
    {
        return $"{workKey}|{languageCode}|{versionTitle}";
    }

    private static string BuildJsonFileName(string workKey, string languageCode, string versionTitle)
    {
        if (string.IsNullOrWhiteSpace(versionTitle))
        {
            return $"{SefariaFileNames.SanitizeForFileName(workKey)}__default.json";
        }

        var versionSegment = SefariaFileNames.SanitizeForFileName($"{languageCode}_{versionTitle}");
        return $"{SefariaFileNames.SanitizeForFileName(workKey)}__{versionSegment}.json";
    }

    private static string NormalizeLanguageCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "en";
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeLookupToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static MongoBsonDocument GetDocument(MongoBsonDocument doc, string key)
    {
        if (doc.TryGetValue(key, out var value) && value.IsBsonDocument)
        {
            return value.AsBsonDocument;
        }

        return new MongoBsonDocument();
    }

    private static MongoBsonArray GetArray(MongoBsonDocument doc, string key)
    {
        if (doc.TryGetValue(key, out var value) && value.IsBsonArray)
        {
            return value.AsBsonArray;
        }

        return new MongoBsonArray();
    }

    private static string GetString(MongoBsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || value.IsBsonNull)
        {
            return string.Empty;
        }

        return value.IsString ? value.AsString : value.ToString() ?? string.Empty;
    }

    private static int GetInt(MongoBsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || !value.IsNumeric)
        {
            return 0;
        }

        return value.ToInt32();
    }

    private static double GetDouble(MongoBsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value) || !value.IsNumeric)
        {
            return 0;
        }

        return value.ToDouble();
    }

    private static bool GetBool(MongoBsonDocument doc, string key)
    {
        if (!doc.TryGetValue(key, out var value))
        {
            return false;
        }

        return value.IsBoolean && value.AsBoolean;
    }

    private static string GetPrimaryTitle(MongoBsonDocument schema, string language)
    {
        foreach (var titleDoc in GetArray(schema, "titles")
                     .Where(item => item.IsBsonDocument)
                     .Select(item => item.AsBsonDocument))
        {
            if (!string.Equals(GetString(titleDoc, "lang"), language, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (GetBool(titleDoc, "primary"))
            {
                return GetString(titleDoc, "text");
            }
        }

        return string.Empty;
    }

    private static List<string> GetStringList(MongoBsonDocument doc, string key)
    {
        return GetArray(doc, key)
            .Select(item => item.IsString ? item.AsString : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void AddIfPresent(ISet<string> set, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }
}
