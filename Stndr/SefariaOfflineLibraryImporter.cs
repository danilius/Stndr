using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace Stndr;

/// <summary>
/// Streams the selected collections directly from Sefaria's tar.gz Mongo dump into SQLite.
/// The expanded dump is never written to disk.
/// </summary>
public sealed class SefariaOfflineLibraryImporter
{
    public const int SchemaVersion = 3;
    private const int ProgressInterval = 50_000;
    private static readonly JsonWriterSettings CompactJson = new()
    {
        Indent = false,
        OutputMode = JsonOutputMode.RelaxedExtendedJson
    };

    private readonly Dictionary<string, long> _workIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _referenceIds = new(StringComparer.Ordinal);
    private readonly List<string> _referencesById = [""];
    private readonly Dictionary<string, long> _lexiconIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> _lexiconEntryIds = new(StringComparer.Ordinal);
    private Dictionary<string, string[]> _workCandidates = new(StringComparer.Ordinal);
    private long _nextWorkId;
    private long _nextVersionId;
    private long _nextReferenceId;
    private long _nextLinkId;
    private long _linkEndpoints;
    private long _segments;
    private long _nextLexiconId;
    private long _nextLexiconEntryId;
    private long _nextDictionaryWordFormId;
    private static readonly Regex DictionaryHtmlRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex DictionaryPipeMarkupRegex = new(@"\|[A-Z]{1,3}([^|;]*)\|[A-Za-z]{1,3};", RegexOptions.Compiled);

    public async Task<SefariaOfflineLibraryImportResult> ImportAsync(
        string archivePath,
        string databasePath,
        IProgress<SefariaOfflineLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("The Sefaria archive was not found.", archivePath);

        ResetState();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        DeleteDatabaseFiles(databasePath);
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await ConfigureAndCreateSchemaAsync(connection, cancellationToken);

        progress?.Report(new(SefariaOfflineLibraryStage.ReadingArchive, "Opening the compressed Sefaria archive"));
        await using var file = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var tar = new TarReader(gzip, leaveOpen: false);

        var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var collection = Path.GetFileNameWithoutExtension(entry.Name);
            if (!IsAllowedCollection(collection)) continue;
            var dataStream = entry.DataStream ?? Stream.Null;

            switch (collection.ToLowerInvariant())
            {
                case "category":
                    await ImportGenericMetadataAsync(connection, dataStream, "categories", progress, cancellationToken);
                    break;
                case "index":
                    await ImportWorksAsync(connection, dataStream, progress, cancellationToken);
                    BuildWorkCandidates();
                    if (_nextLinkId > 0)
                        await BackfillLinkWorkIdsAsync(connection, progress, cancellationToken);
                    break;
                case "links":
                    await ImportLinksAsync(connection, dataStream, progress, cancellationToken);
                    break;
                case "lexicon":
                    await ImportLexiconsAsync(connection, dataStream, progress, cancellationToken);
                    break;
                case "lexicon_entry":
                    await ImportLexiconEntriesAsync(connection, dataStream, progress, cancellationToken);
                    break;
                case "person":
                    await ImportGenericMetadataAsync(connection, dataStream, "people", progress, cancellationToken);
                    break;
                case "term":
                    await ImportGenericMetadataAsync(connection, dataStream, "terms", progress, cancellationToken);
                    break;
                case "texts":
                    await ImportVersionsAsync(connection, dataStream, progress, cancellationToken);
                    break;
                case "word_form":
                    await ImportDictionaryWordFormsAsync(connection, dataStream, progress, cancellationToken);
                    break;
            }
            imported.Add(collection);
        }

        var required = new[] { "index", "links", "texts" };
        var missing = required.Where(name => !imported.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"The archive is missing required collections: {string.Join(", ", missing)}.");
        }

        progress?.Report(new(SefariaOfflineLibraryStage.BuildingIndexes, "Building offline library indexes"));
        await CreateFinalIndexesAsync(connection, cancellationToken);
        await SetMetadataAsync(connection, "completed_utc", DateTime.UtcNow.ToString("O"), cancellationToken);
        await SetMetadataAsync(connection, "source_archive_bytes", file.Length.ToString(), cancellationToken);
        await connection.CloseAsync();

        stopwatch.Stop();
        return new(
            checked((int)_workIds.Count), checked((int)_nextVersionId), _nextLinkId,
            _linkEndpoints, checked((int)_referenceIds.Count), _segments,
            checked((int)_nextLexiconId), checked((int)_nextLexiconEntryId), checked((int)_nextDictionaryWordFormId),
            new FileInfo(databasePath).Length, stopwatch.Elapsed);
    }

    private void ResetState()
    {
        _workIds.Clear();
        _referenceIds.Clear();
        _lexiconIds.Clear();
        _lexiconEntryIds.Clear();
        _referencesById.Clear();
        _referencesById.Add("");
        _workCandidates = new(StringComparer.Ordinal);
        _nextWorkId = _nextVersionId = _nextReferenceId = _nextLinkId = _linkEndpoints = _segments = 0;
        _nextLexiconId = _nextLexiconEntryId = _nextDictionaryWordFormId = 0;
    }

    private static bool IsAllowedCollection(string name) => name.Equals("category", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("index", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("links", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("lexicon", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("lexicon_entry", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("person", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("term", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("texts", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("word_form", StringComparison.OrdinalIgnoreCase);

    private static async Task ConfigureAndCreateSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        var sql = $"""
            PRAGMA page_size=8192;
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA locking_mode=EXCLUSIVE;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-524288;
            PRAGMA foreign_keys=OFF;
            CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL) WITHOUT ROWID;
            INSERT INTO metadata VALUES('schema_version','{SchemaVersion}');
            INSERT INTO metadata VALUES('created_utc','{DateTime.UtcNow:O}');
            CREATE TABLE works(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL,title TEXT NOT NULL UNIQUE,he_title TEXT NOT NULL,categories_json TEXT NOT NULL,schema_json TEXT NOT NULL,alt_structs_json TEXT NOT NULL,dependence TEXT NOT NULL,collective_title TEXT NOT NULL,authors_json TEXT NOT NULL,en_description TEXT NOT NULL,he_description TEXT NOT NULL,en_short_description TEXT NOT NULL,he_short_description TEXT NOT NULL);
            CREATE TABLE terms(id INTEGER PRIMARY KEY,data_json TEXT NOT NULL);
            CREATE TABLE categories(id INTEGER PRIMARY KEY,data_json TEXT NOT NULL);
            CREATE TABLE people(id INTEGER PRIMARY KEY,data_json TEXT NOT NULL);
            CREATE TABLE versions(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL,work_id INTEGER NOT NULL,language TEXT NOT NULL,actual_language TEXT NOT NULL,language_family TEXT NOT NULL,version_title TEXT NOT NULL,version_title_hebrew TEXT NOT NULL,version_source TEXT NOT NULL,license TEXT NOT NULL,status TEXT NOT NULL,direction TEXT NOT NULL,is_primary INTEGER NOT NULL,is_source INTEGER NOT NULL,priority REAL NOT NULL,text_shape TEXT NOT NULL,segment_count INTEGER NOT NULL,character_count INTEGER NOT NULL,uncompressed_bytes INTEGER NOT NULL,compressed_bytes INTEGER NOT NULL,text_sha256 BLOB NOT NULL,content_zlib BLOB NOT NULL,metadata_json TEXT NOT NULL,UNIQUE(work_id,language,version_title));
            CREATE TABLE refs(id INTEGER PRIMARY KEY,reference TEXT NOT NULL UNIQUE);
            CREATE TABLE links(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL,ref0_id INTEGER NOT NULL,ref1_id INTEGER NOT NULL,work0_id INTEGER,work1_id INTEGER,link_type TEXT NOT NULL,anchor_text TEXT NOT NULL,is_auto INTEGER NOT NULL,generated_by TEXT NOT NULL,available0 INTEGER NOT NULL,available1 INTEGER NOT NULL,inline_citation INTEGER NOT NULL,extra_json TEXT NOT NULL);
            CREATE TABLE link_endpoints(ref_id INTEGER NOT NULL,link_id INTEGER NOT NULL,side INTEGER NOT NULL,PRIMARY KEY(ref_id,link_id,side)) WITHOUT ROWID;
            CREATE TABLE lexicons(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL,name TEXT NOT NULL UNIQUE,language TEXT NOT NULL,to_language TEXT NOT NULL,text_categories_json TEXT NOT NULL,source TEXT NOT NULL,source_url TEXT NOT NULL,attribution TEXT NOT NULL,attribution_url TEXT NOT NULL,data_json TEXT NOT NULL);
            CREATE TABLE lexicon_entries(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL UNIQUE,lexicon_id INTEGER NOT NULL,headword TEXT NOT NULL,headword_key TEXT NOT NULL,sort_key TEXT NOT NULL,transliteration TEXT NOT NULL,pronunciation TEXT NOT NULL,language_code TEXT NOT NULL,morphology TEXT NOT NULL,definition_text TEXT NOT NULL,content_json TEXT NOT NULL,metadata_json TEXT NOT NULL,ordinal INTEGER NOT NULL,rid TEXT NOT NULL,prev_hw TEXT NOT NULL,next_hw TEXT NOT NULL,strong_number TEXT NOT NULL,gk TEXT NOT NULL,twot TEXT NOT NULL,root TEXT NOT NULL);
            CREATE TABLE lexicon_aliases(entry_id INTEGER NOT NULL,alias TEXT NOT NULL,alias_key TEXT NOT NULL,kind INTEGER NOT NULL,PRIMARY KEY(entry_id,alias,kind)) WITHOUT ROWID;
            CREATE TABLE dictionary_word_forms(id INTEGER PRIMARY KEY,upstream_id TEXT NOT NULL UNIQUE,form TEXT NOT NULL,form_key TEXT NOT NULL,consonantal_form TEXT NOT NULL,language_code TEXT NOT NULL,refs_json TEXT NOT NULL);
            CREATE TABLE dictionary_word_form_entries(form_id INTEGER NOT NULL,entry_id INTEGER NOT NULL,PRIMARY KEY(form_id,entry_id)) WITHOUT ROWID;
            """;
        await ExecuteAsync(connection, sql, token);
    }

    private async Task ImportWorksAsync(SqliteConnection connection, Stream stream, IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingMetadata, "Importing books and schemas..."));
        using var transaction = connection.BeginTransaction();
        using var insert = new PreparedInsert(connection, transaction,
            "INSERT INTO works VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13)", 14);
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            var title = String(document, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var id = ++_nextWorkId;
            _workIds[title] = id;
            var schema = Value(document, "schema", new BsonDocument());
            await insert.ExecuteAsync(token, id, UpstreamId(document), title, PrimaryTitle(schema, "he"), Json(Value(document, "categories", new BsonArray())),
                Json(schema), Json(Value(document, "alt_structs", new BsonDocument())), String(document, "dependence"),
                String(document, "collective_title"), Json(Value(document, "authors", new BsonArray())), String(document, "enDesc"),
                String(document, "heDesc"), String(document, "enShortDesc"), String(document, "heShortDesc"));
            if (_nextWorkId % 500 == 0)
            {
                progress?.Report(new(
                    SefariaOfflineLibraryStage.ImportingMetadata,
                    $"Importing books and schemas... ({_nextWorkId:N0})",
                    _nextWorkId));
                await Task.Yield();
            }
        }

        progress?.Report(new(
            SefariaOfflineLibraryStage.ImportingMetadata,
            $"Imported {_nextWorkId:N0} books and schemas",
            _nextWorkId));
        transaction.Commit();
    }

    private static async Task ImportGenericMetadataAsync(SqliteConnection connection, Stream stream, string table,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        var label = table switch
        {
            "categories" => "library categories",
            "people" => "author records",
            "terms" => "titles and terms",
            _ => table
        };
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingMetadata, $"Importing {label}..."));
        using var transaction = connection.BeginTransaction();
        using var insert = new PreparedInsert(connection, transaction, $"INSERT INTO {table} VALUES($p0,$p1)", 2);
        long id = 0;
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            document.Remove("_id");
            await insert.ExecuteAsync(token, ++id, Json(document));
            // Report often enough that long metadata stages (especially terms) keep the UI alive.
            if (id % 2_000 == 0)
            {
                progress?.Report(new(
                    SefariaOfflineLibraryStage.ImportingMetadata,
                    $"Importing {label}... ({id:N0})",
                    id));
                await Task.Yield();
            }
        }

        progress?.Report(new(
            SefariaOfflineLibraryStage.ImportingMetadata,
            $"Imported {id:N0} {label}",
            id));
        transaction.Commit();
    }

    private async Task ImportLexiconsAsync(SqliteConnection connection, Stream stream,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingDictionaries, "Importing dictionary catalogue"));
        using var transaction = connection.BeginTransaction();
        using var insert = new PreparedInsert(connection, transaction, "INSERT INTO lexicons VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10)", 11);
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            var name = String(document, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = ++_nextLexiconId;
            _lexiconIds[name] = id;
            var metadata = document.DeepClone().AsBsonDocument;
            metadata.Remove("_id");
            await insert.ExecuteAsync(token, id, UpstreamId(document), name, String(document, "language"),
                String(document, "to_language"), Json(Value(document, "text_categories", new BsonArray())),
                String(document, "source"), String(document, "source_url"), String(document, "attribution"),
                String(document, "attribution_url"), Json(metadata));
        }
        transaction.Commit();
    }

    private async Task ImportLexiconEntriesAsync(SqliteConnection connection, Stream stream,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingDictionaries, "Importing dictionary entries"));
        using var transaction = connection.BeginTransaction();
        using var entryInsert = new PreparedInsert(connection, transaction,
            "INSERT INTO lexicon_entries VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15,$p16,$p17,$p18,$p19,$p20)", 21);
        using var aliasInsert = new PreparedInsert(connection, transaction,
            "INSERT OR IGNORE INTO lexicon_aliases VALUES($p0,$p1,$p2,$p3)", 4);
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            var parent = String(document, "parent_lexicon");
            var headword = String(document, "headword");
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(headword)) continue;
            if (!_lexiconIds.TryGetValue(parent, out var lexiconId))
            {
                lexiconId = ++_nextLexiconId;
                _lexiconIds[parent] = lexiconId;
                using var lexiconInsert = new PreparedInsert(connection, transaction,
                    "INSERT INTO lexicons VALUES($p0,'',$p1,'','','[]','','','','','{}')", 2);
                await lexiconInsert.ExecuteAsync(token, lexiconId, parent);
            }

            var id = ++_nextLexiconEntryId;
            var content = Value(document, "content", new BsonDocument());
            var definitions = new List<string>();
            CollectDictionaryDefinitions(content, definitions);
            var definitionText = string.Join(Environment.NewLine + Environment.NewLine, definitions
                .Select(CleanDictionaryText).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
            var metadata = document.DeepClone().AsBsonDocument;
            metadata.Remove("_id");
            metadata.Remove("content");
            var headwordKey = NormalizeDictionaryKey(headword, keepSpaces: true);
            var sortKey = NormalizeDictionaryKey(headword, keepSpaces: false);
            await entryInsert.ExecuteAsync(token, id, UpstreamId(document), lexiconId, headword, headwordKey, sortKey,
                String(document, "transliteration"), String(document, "pronunciation"), String(document, "language_code"),
                Json(Value(document, "morphology", BsonNull.Value)), definitionText, Json(content), Json(metadata),
                Long(document, "ordinal"), String(document, "rid"), String(document, "prev_hw"), String(document, "next_hw"),
                String(document, "strong_number"), String(document, "gk"), String(document, "twot"), String(document, "root"));

            AddLexiconEntryMap(parent, headword, id);
            await aliasInsert.ExecuteAsync(token, id, headword, headwordKey, 0);
            foreach (var alias in ReadDictionaryAliases(document))
            {
                await aliasInsert.ExecuteAsync(token, id, alias, NormalizeDictionaryKey(alias, keepSpaces: true), 1);
                AddLexiconEntryMap(parent, alias, id);
            }
            if (id % 10_000 == 0)
                progress?.Report(new(SefariaOfflineLibraryStage.ImportingDictionaries, $"Imported {id:N0} dictionary entries", id));
        }
        transaction.Commit();
    }

    private async Task ImportDictionaryWordFormsAsync(SqliteConnection connection, Stream stream,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingDictionaries, "Importing dictionary word forms and reference context"));
        using var transaction = connection.BeginTransaction();
        using var formInsert = new PreparedInsert(connection, transaction,
            "INSERT INTO dictionary_word_forms VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", 7);
        using var linkInsert = new PreparedInsert(connection, transaction,
            "INSERT OR IGNORE INTO dictionary_word_form_entries VALUES($p0,$p1)", 2);
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            var form = String(document, "form");
            if (string.IsNullOrWhiteSpace(form)) continue;
            var id = ++_nextDictionaryWordFormId;
            await formInsert.ExecuteAsync(token, id, UpstreamId(document), form,
                NormalizeDictionaryKey(form, keepSpaces: true), NormalizeDictionaryKey(String(document, "c_form"), keepSpaces: true),
                String(document, "language_code"), Json(Value(document, "refs", new BsonArray())));
            if (document.TryGetValue("lookups", out var value) && value is BsonArray lookups)
            foreach (var lookup in lookups.OfType<BsonDocument>())
            {
                var key = LexiconEntryMapKey(String(lookup, "parent_lexicon"), String(lookup, "headword"));
                if (_lexiconEntryIds.TryGetValue(key, out var entryIds))
                    foreach (var entryId in entryIds) await linkInsert.ExecuteAsync(token, id, entryId);
            }
            if (id % 25_000 == 0)
                progress?.Report(new(SefariaOfflineLibraryStage.ImportingDictionaries, $"Imported {id:N0} dictionary word forms", id));
        }
        transaction.Commit();
    }

    private void AddLexiconEntryMap(string lexicon, string headword, long id)
    {
        var key = LexiconEntryMapKey(lexicon, headword);
        if (!_lexiconEntryIds.TryGetValue(key, out var ids)) _lexiconEntryIds[key] = ids = new();
        if (!ids.Contains(id)) ids.Add(id);
    }

    private static string LexiconEntryMapKey(string lexicon, string headword) => lexicon + "\u001f" + headword;

    private static IEnumerable<string> ReadDictionaryAliases(BsonDocument document)
    {
        if (!document.TryGetValue("alt_headwords", out var value) || value is not BsonArray aliases) yield break;
        foreach (var alias in aliases)
        {
            var text = alias switch
            {
                BsonString bsonString => bsonString.Value,
                BsonDocument bsonDocument => String(bsonDocument, "word", String(bsonDocument, "headword")),
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
        }
    }

    private static void CollectDictionaryDefinitions(BsonValue value, List<string> definitions)
    {
        if (value is BsonDocument document)
        {
            foreach (var element in document.Elements)
            {
                if (element.Name.Equals("definition", StringComparison.OrdinalIgnoreCase) && element.Value is BsonString text)
                    definitions.Add(text.Value);
                else CollectDictionaryDefinitions(element.Value, definitions);
            }
        }
        else if (value is BsonArray array)
            foreach (var item in array) CollectDictionaryDefinitions(item, definitions);
    }

    private static string CleanDictionaryText(string value)
    {
        var pipeCleaned = DictionaryPipeMarkupRegex.Replace(value, "$1");
        var withoutHtml = DictionaryHtmlRegex.Replace(pipeCleaned, " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutHtml), @"\s+", " ").Trim();
    }

    internal static string NormalizeDictionaryKey(string value, bool keepSpaces = true)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            var normalized = character switch { 'ך' => 'כ', 'ם' => 'מ', 'ן' => 'נ', 'ף' => 'פ', 'ץ' => 'צ', _ => char.ToLowerInvariant(character) };
            if (char.IsLetterOrDigit(normalized))
            {
                if (keepSpaces && pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(normalized);
                pendingSpace = false;
            }
            else if (keepSpaces) pendingSpace = true;
        }
        return builder.ToString();
    }

    private async Task ImportVersionsAsync(SqliteConnection connection, Stream stream,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingTexts, "Importing and compressing every text version"));
        using var transaction = connection.BeginTransaction();
        using var insert = new PreparedInsert(connection, transaction,
            "INSERT INTO versions VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15,$p16,$p17,$p18,$p19,$p20,$p21,$p22)", 23);
        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            var title = String(document, "title");
            if (!_workIds.TryGetValue(title, out var workId))
            {
                workId = await AddSyntheticWorkAsync(connection, transaction, title, token);
            }
            var content = Value(document, "chapter", BsonNull.Value);
            var jsonBytes = Encoding.UTF8.GetBytes(Json(content));
            var compressed = Compress(jsonBytes);
            var stats = TextStats(content);
            _segments += stats.Segments;
            var upstreamId = UpstreamId(document);
            document.Remove("_id");
            document.Remove("chapter");
            var language = String(document, "language");
            await insert.ExecuteAsync(token,
                ++_nextVersionId, upstreamId, workId, language, String(document, "actualLanguage", language), String(document, "languageFamilyName"),
                String(document, "versionTitle"), String(document, "versionTitleInHebrew"), String(document, "versionSource"),
                String(document, "license"), String(document, "status"), String(document, "direction"), Bool(document, "isPrimary"),
                Bool(document, "isSource"), Double(document, "priority"), stats.Shape, stats.Segments, stats.Characters,
                jsonBytes.Length, compressed.Length, SHA256.HashData(jsonBytes), compressed, Json(document));
            if (_nextVersionId % 250 == 0)
            {
                progress?.Report(new(SefariaOfflineLibraryStage.ImportingTexts,
                    $"Imported {_nextVersionId:N0} versions ({_segments:N0} text segments)", _nextVersionId));
            }
        }
        transaction.Commit();
    }

    private async Task<long> AddSyntheticWorkAsync(SqliteConnection connection, SqliteTransaction transaction, string title, CancellationToken token)
    {
        var id = ++_nextWorkId;
        _workIds[title] = id;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO works VALUES($id,'',$title,'','[]','{}','{}','','','[]','','','','')";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        await command.ExecuteNonQueryAsync(token);
        return id;
    }

    private async Task ImportLinksAsync(SqliteConnection connection, Stream stream,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingLinks, "Importing links and expanded link endpoints"));
        using var transaction = connection.BeginTransaction();
        using var refInsert = new PreparedInsert(connection, transaction, "INSERT INTO refs VALUES($p0,$p1)", 2);
        using var linkInsert = new PreparedInsert(connection, transaction,
            "INSERT INTO links VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13)", 14);
        using var endpointInsert = new PreparedInsert(connection, transaction,
            "INSERT OR IGNORE INTO link_endpoints VALUES($p0,$p1,$p2)", 3);

        await foreach (var document in ReadBsonDocumentsAsync(stream, token))
        {
            if (!document.TryGetValue("refs", out var refsValue) || refsValue is not BsonArray refs || refs.Count < 2) continue;
            var ref0 = (refs[0]?.ToString() ?? "").Trim();
            var ref1 = (refs[1]?.ToString() ?? "").Trim();
            var ref0Id = await GetReferenceIdAsync(ref0, refInsert, token);
            var ref1Id = await GetReferenceIdAsync(ref1, refInsert, token);
            var linkId = ++_nextLinkId;
            var extra = new BsonDocument();
            foreach (var key in new[] { "inline_reference", "charLevelData", "score", "versions", "displayedText" })
                if (document.TryGetValue(key, out var value)) extra[key] = value;
            await linkInsert.ExecuteAsync(token, linkId, UpstreamId(document), ref0Id, ref1Id, ResolveWorkId(ref0), ResolveWorkId(ref1),
                String(document, "type", "Other"), String(document, "anchorText"), Bool(document, "auto"),
                String(document, "generated_by"), LanguageMask(document, 0), LanguageMask(document, 1),
                Bool(document, "inline_citation"), extra.ElementCount == 0 ? "" : Json(extra));
            await AddEndpointsAsync(document, "expandedRefs0", ref0Id, linkId, 0, refInsert, endpointInsert, token);
            await AddEndpointsAsync(document, "expandedRefs1", ref1Id, linkId, 1, refInsert, endpointInsert, token);
            if (linkId % ProgressInterval == 0)
            {
                progress?.Report(new(SefariaOfflineLibraryStage.ImportingLinks,
                    $"Imported {linkId:N0} links ({_linkEndpoints:N0} endpoints)", linkId));
            }
        }
        transaction.Commit();
    }

    private async Task AddEndpointsAsync(BsonDocument document, string key, long fallbackId, long linkId, int side,
        PreparedInsert refInsert, PreparedInsert endpointInsert, CancellationToken token)
    {
        var ids = new HashSet<long>();
        if (document.TryGetValue(key, out var expandedValue) && expandedValue is BsonArray expanded && expanded.Count > 0)
        {
            foreach (var value in expanded)
                ids.Add(await GetReferenceIdAsync((value?.ToString() ?? "").Trim(), refInsert, token));
        }
        else ids.Add(fallbackId);
        foreach (var id in ids)
        {
            await endpointInsert.ExecuteAsync(token, id, linkId, side);
            _linkEndpoints++;
        }
    }

    private async Task<long> GetReferenceIdAsync(string reference, PreparedInsert insert, CancellationToken token)
    {
        if (_referenceIds.TryGetValue(reference, out var id)) return id;
        id = ++_nextReferenceId;
        _referenceIds.Add(reference, id);
        _referencesById.Add(reference);
        await insert.ExecuteAsync(token, id, reference);
        return id;
    }

    private async Task BackfillLinkWorkIdsAsync(SqliteConnection connection,
        IProgress<SefariaOfflineLibraryProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(SefariaOfflineLibraryStage.ImportingLinks, "Connecting imported links to their books"));
        const int batchSize = 100_000;
        long lastId = 0;
        while (true)
        {
            var rows = new List<(long Id, long Ref0Id, long Ref1Id)>(batchSize);
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id,ref0_id,ref1_id FROM links WHERE id>$last ORDER BY id LIMIT $limit";
                select.Parameters.AddWithValue("$last", lastId);
                select.Parameters.AddWithValue("$limit", batchSize);
                await using var reader = await select.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
            if (rows.Count == 0) break;

            using var transaction = connection.BeginTransaction();
            using var update = new PreparedInsert(connection, transaction,
                "UPDATE links SET work0_id=$p0,work1_id=$p1 WHERE id=$p2", 3);
            foreach (var row in rows)
            {
                await update.ExecuteAsync(token,
                    ResolveWorkId(_referencesById[checked((int)row.Ref0Id)]),
                    ResolveWorkId(_referencesById[checked((int)row.Ref1Id)]), row.Id);
            }
            transaction.Commit();
            lastId = rows[^1].Id;
            progress?.Report(new(SefariaOfflineLibraryStage.ImportingLinks,
                $"Connected {lastId:N0} links to book metadata", lastId, _nextLinkId));
        }
    }

    private void BuildWorkCandidates()
    {
        _workCandidates = _workIds.Keys
            .GroupBy(title => title.Split(' ', 2)[0].TrimEnd(','), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(title => title.Length).ToArray(), StringComparer.Ordinal);
    }

    private object ResolveWorkId(string reference)
    {
        var first = reference.Split(' ', 2)[0].TrimEnd(',');
        if (_workCandidates.TryGetValue(first, out var candidates))
            foreach (var title in candidates)
                if (reference == title || reference.StartsWith(title + " ", StringComparison.Ordinal) || reference.StartsWith(title + ",", StringComparison.Ordinal))
                    return _workIds[title];
        return DBNull.Value;
    }

    private static async Task CreateFinalIndexesAsync(SqliteConnection connection, CancellationToken token)
    {
        await ExecuteAsync(connection, """
            CREATE INDEX ix_versions_work_language ON versions(work_id,language,priority DESC);
            CREATE UNIQUE INDEX ix_versions_upstream_id ON versions(upstream_id) WHERE upstream_id<>'';
            CREATE UNIQUE INDEX ix_works_upstream_id ON works(upstream_id) WHERE upstream_id<>'';
            CREATE UNIQUE INDEX ix_links_upstream_id ON links(upstream_id) WHERE upstream_id<>'';
            CREATE INDEX ix_versions_license ON versions(license);
            CREATE INDEX ix_links_work0 ON links(work0_id);
            CREATE INDEX ix_links_work1 ON links(work1_id);
            CREATE INDEX ix_link_endpoints_link ON link_endpoints(link_id,side);
            CREATE INDEX ix_lexicon_entries_browse ON lexicon_entries(lexicon_id,sort_key,id);
            CREATE INDEX ix_lexicon_entries_headword_key ON lexicon_entries(headword_key);
            CREATE INDEX ix_lexicon_aliases_key ON lexicon_aliases(alias_key,entry_id);
            CREATE INDEX ix_dictionary_word_forms_key ON dictionary_word_forms(form_key,id);
            CREATE INDEX ix_dictionary_word_forms_consonantal ON dictionary_word_forms(consonantal_form,id);
            CREATE INDEX ix_dictionary_word_form_entries_entry ON dictionary_word_form_entries(entry_id,form_id);
            CREATE VIRTUAL TABLE lexicon_search USING fts5(headword,transliteration,definition_text,identifiers,tokenize='unicode61 remove_diacritics 2');
            INSERT INTO lexicon_search(rowid,headword,transliteration,definition_text,identifiers)
            SELECT id,headword,transliteration,definition_text,trim(strong_number||' '||gk||' '||twot||' '||root||' '||rid) FROM lexicon_entries;
            PRAGMA optimize;
            """, token);
    }

    private static async Task SetMetadataAsync(SqliteConnection connection, string key, string value, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO metadata VALUES($key,$value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async IAsyncEnumerable<BsonDocument> ReadBsonDocumentsAsync(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var header = new byte[4];
        while (true)
        {
            var headerRead = await ReadAtMostAsync(stream, header, token);
            if (headerRead == 0) yield break;
            if (headerRead != 4) throw new InvalidDataException("A BSON document header was truncated.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 5 || length > 64 * 1024 * 1024) throw new InvalidDataException($"Invalid BSON document length: {length:N0}.");
            var buffer = new byte[length];
            header.CopyTo(buffer, 0);
            await ReadExactlyAsync(stream, buffer.AsMemory(4), token);
            yield return BsonSerializer.Deserialize<BsonDocument>(buffer);
        }
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], token);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        if (await ReadAtMostAsync(stream, buffer, token) != buffer.Length)
            throw new InvalidDataException("A BSON document was truncated.");
    }

    private static BsonValue Value(BsonDocument document, string key, BsonValue fallback) =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull ? value : fallback;
    private static string UpstreamId(BsonDocument document) => String(document, "_id");
    private static string String(BsonDocument document, string key, string fallback = "") =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull ? value.ToString() ?? fallback : fallback;
    private static long Bool(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && value.ToBoolean() ? 1 : 0;
    private static double Double(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull ? value.ToDouble() : 0;
    private static long Long(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && !value.IsBsonNull && value.IsNumeric ? value.ToInt64() : 0;
    private static string Json(BsonValue value) => value.ToJson(CompactJson);

    private static string PrimaryTitle(BsonValue schema, string language)
    {
        if (schema is not BsonDocument doc || !doc.TryGetValue("titles", out var titlesValue) || titlesValue is not BsonArray titles) return "";
        string fallback = "";
        foreach (var value in titles)
        {
            if (value is not BsonDocument title || String(title, "lang") != language) continue;
            fallback = String(title, "text", fallback);
            if (Bool(title, "primary") == 1) return fallback;
        }
        return fallback;
    }

    private static int LanguageMask(BsonDocument document, int side)
    {
        if (!document.TryGetValue("availableLangs", out var value) || value is not BsonArray sides || side >= sides.Count || sides[side] is not BsonArray languages) return 0;
        var mask = 0;
        foreach (var language in languages.Select(item => (item?.ToString() ?? "").ToLowerInvariant()))
            mask |= language switch { "en" => 1, "he" => 2, _ when language.Length > 0 => 4, _ => 0 };
        return mask;
    }

    private static (string Shape, long Segments, long Characters) TextStats(BsonValue value)
    {
        long segments = 0, characters = 0;
        void Walk(BsonValue current)
        {
            if (current is BsonArray array) { foreach (var child in array) Walk(child); return; }
            if (current is BsonDocument document) { foreach (var child in document.Values) Walk(child); return; }
            if (current is not BsonString bsonString) return;
            var text = bsonString.Value;
            if (!string.IsNullOrWhiteSpace(text)) { segments++; characters += text.Length; }
        }
        Walk(value);
        var shape = value switch { BsonArray => "array", BsonDocument => "object", _ => "scalar" };
        return (shape, segments, characters);
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(source);
        return output.ToArray();
    }

    internal static void DeleteDatabaseFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            if (File.Exists(candidate)) File.Delete(candidate);
    }

    private sealed class PreparedInsert : IDisposable
    {
        private readonly SqliteCommand _command;
        public PreparedInsert(SqliteConnection connection, SqliteTransaction transaction, string sql, int count)
        {
            _command = connection.CreateCommand();
            _command.Transaction = transaction;
            _command.CommandText = sql;
            for (var i = 0; i < count; i++) _command.Parameters.Add(new SqliteParameter($"$p{i}", null));
            _command.Prepare();
        }
        public async Task ExecuteAsync(CancellationToken token, params object?[] values)
        {
            for (var i = 0; i < values.Length; i++) _command.Parameters[i].Value = values[i] ?? DBNull.Value;
            await _command.ExecuteNonQueryAsync(token);
        }
        public void Dispose() => _command.Dispose();
    }
}
