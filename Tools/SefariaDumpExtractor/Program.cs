using System.Formats.Tar;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException argumentException)
{
    Console.Error.WriteLine(argumentException.Message);
    CliOptions.PrintUsage();
    return 2;
}

if (options.ShowHelp)
{
    CliOptions.PrintUsage();
    return 0;
}

if (!File.Exists(options.DumpPath))
{
    Console.Error.WriteLine($"Dump archive not found: {options.DumpPath}");
    return 2;
}

Directory.CreateDirectory(options.OutputRoot);
var checkpointPath = options.CheckpointPath ?? Path.Combine(options.OutputRoot, "checkpoint.json");
var metadataPath = Path.Combine(options.OutputRoot, "export-metadata.ndjson");

var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var progress = ExtractionProgress.Load(checkpointPath, options.DumpPath);
var startedAt = DateTime.UtcNow;

var indexByTitle = new Dictionary<string, IndexMetadata>(StringComparer.OrdinalIgnoreCase);
var summary = new ExtractionSummary
{
    RunId = runId,
    DumpPath = options.DumpPath,
    OutputRoot = options.OutputRoot,
    StartedAtUtc = startedAt,
    ResumedFromSequence = progress.LastSequenceProcessed
};

Console.WriteLine($"Starting extraction from {options.DumpPath}");
Console.WriteLine($"Output root: {options.OutputRoot}");
Console.WriteLine($"Resume sequence: {progress.LastSequenceProcessed}");

ReadIndexCollectionFromArchive(options.DumpPath, indexByTitle, summary);

var stoppedByMaxDocs = false;
using var metadataSink = new NdjsonMetadataSink(metadataPath, runId);
using (var textsStream = OpenTextsCollectionStream(options.DumpPath))
{
    var sequence = 0L;
    foreach (var document in ReadBsonDocuments(textsStream))
    {
        sequence++;
        summary.TotalTextDocumentsSeen = sequence;

        if (sequence <= progress.LastSequenceProcessed)
        {
            summary.SkippedByCheckpoint++;
            continue;
        }

        var exportRecord = BuildExportRecord(document, indexByTitle, sequence);
        var targetPath = BuildTargetFilePath(options.OutputRoot, exportRecord);
        exportRecord.RelativePath = Path.GetRelativePath(options.OutputRoot, targetPath);

        if (File.Exists(targetPath) && !options.Overwrite)
        {
            summary.SkippedExistingFiles++;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await WriteExportFileAsync(targetPath, exportRecord, document);
            await metadataSink.WriteAsync(exportRecord);
            summary.ExportedDocuments++;
        }

        summary.LastSequenceProcessed = sequence;
        progress.LastSequenceProcessed = sequence;

        if (sequence % options.CheckpointEvery == 0)
        {
            progress.Save(checkpointPath, options.DumpPath, false);
            Console.WriteLine($"Checkpoint saved at sequence {sequence} (exported {summary.ExportedDocuments}).");
        }

        if (options.MaxDocuments > 0 && summary.ExportedDocuments >= options.MaxDocuments)
        {
            Console.WriteLine("Reached --max-docs limit, stopping early.");
            stoppedByMaxDocs = true;
            break;
        }
    }
}

summary.CompletedAtUtc = DateTime.UtcNow;
summary.DurationSeconds = (summary.CompletedAtUtc - summary.StartedAtUtc).TotalSeconds;
summary.DocsPerSecond = summary.ExportedDocuments <= 0 || summary.DurationSeconds <= 0
    ? 0
    : summary.ExportedDocuments / summary.DurationSeconds;

progress.LastSequenceProcessed = summary.LastSequenceProcessed;
progress.Save(checkpointPath, options.DumpPath, !stoppedByMaxDocs);

var summaryPath = Path.Combine(options.OutputRoot, $"run-summary-{runId}.json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, JsonSettings.Pretty));

Console.WriteLine($"Done. Exported {summary.ExportedDocuments} documents.");
Console.WriteLine($"Summary: {summaryPath}");
Console.WriteLine($"Metadata index: {metadataPath}");
Console.WriteLine($"Checkpoint: {checkpointPath}");

return 0;

static void ReadIndexCollectionFromArchive(
    string dumpArchivePath,
    Dictionary<string, IndexMetadata> indexByTitle,
    ExtractionSummary summary)
{
    Console.WriteLine("Reading index.bson...");
    using var indexStream = OpenCollectionStream(dumpArchivePath, "index.bson");
    ReadIndexCollection(indexStream, indexByTitle, summary);
}

static Stream OpenTextsCollectionStream(string dumpArchivePath)
{
    Console.WriteLine("Streaming texts.bson...");
    return OpenCollectionStream(dumpArchivePath, "texts.bson");
}

static Stream OpenCollectionStream(string dumpArchivePath, string collectionFileName)
{
    var file = File.OpenRead(dumpArchivePath);
    var gzip = new GZipStream(file, CompressionMode.Decompress);
    var tarReader = new TarReader(gzip);

    TarEntry? entry;
    while ((entry = tarReader.GetNextEntry(copyData: false)) is not null)
    {
        if (entry.EntryType is not TarEntryType.RegularFile ||
            string.IsNullOrWhiteSpace(entry.Name) ||
            !entry.Name.EndsWith(collectionFileName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (entry.DataStream is null)
        {
            tarReader.Dispose();
            gzip.Dispose();
            file.Dispose();
            throw new InvalidDataException($"Archive entry {entry.Name} has no data stream.");
        }

        return new TarEntryReadStream(entry.DataStream, tarReader, gzip, file);
    }

    tarReader.Dispose();
    gzip.Dispose();
    file.Dispose();
    throw new FileNotFoundException($"Collection {collectionFileName} was not found in archive {dumpArchivePath}.");
}

static void ReadIndexCollection(Stream stream, Dictionary<string, IndexMetadata> indexByTitle, ExtractionSummary summary)
{
    foreach (var doc in ReadBsonDocuments(stream))
    {
        var title = ReadString(doc, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            continue;
        }

        var categories = ReadStringArray(doc, "categories");
        var primaryCategory = categories.Count > 0 ? categories[0] : "Unknown";
        var metadata = new IndexMetadata(
            title.Trim(),
            ReadString(doc, "heTitle"),
            categories,
            primaryCategory);
        indexByTitle[metadata.Title] = metadata;
        summary.IndexDocumentsRead++;
    }
}

static IEnumerable<BsonDocument> ReadBsonDocuments(Stream stream)
{
    var lengthPrefix = new byte[4];
    while (true)
    {
        var prefixBytesRead = FillBuffer(stream, lengthPrefix, 4);
        if (prefixBytesRead == 0)
        {
            yield break;
        }

        if (prefixBytesRead < 4)
        {
            throw new InvalidDataException("Unexpected end of BSON stream while reading document length.");
        }

        var docLength = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (docLength < 5)
        {
            throw new InvalidDataException($"Invalid BSON document length {docLength}.");
        }

        var buffer = new byte[docLength];
        lengthPrefix.CopyTo(buffer, 0);
        var bodyRead = FillBuffer(stream, buffer.AsSpan(4), docLength - 4);
        if (bodyRead < docLength - 4)
        {
            throw new InvalidDataException("Unexpected end of BSON stream while reading document body.");
        }
        var doc = BsonSerializer.Deserialize<BsonDocument>(buffer);
        yield return doc;
    }
}

static int FillBuffer(Stream stream, Span<byte> target, int expectedLength)
{
    var totalRead = 0;
    while (totalRead < expectedLength)
    {
        var bytesRead = stream.Read(target[totalRead..expectedLength]);
        if (bytesRead == 0)
        {
            break;
        }

        totalRead += bytesRead;
    }

    return totalRead;
}

static ExportRecord BuildExportRecord(BsonDocument doc, Dictionary<string, IndexMetadata> indexByTitle, long sequence)
{
    var title = ReadString(doc, "title", "book", "indexTitle");
    var language = NormalizeToken(ReadString(doc, "language", "lang"), "unknown");
    var versionTitle = ReadString(doc, "versionTitle", "version", "versionName");
    var reference = ReadString(doc, "ref");
    var documentId = ReadObjectIdAsString(doc, "_id");

    IndexMetadata? index;
    indexByTitle.TryGetValue(title ?? string.Empty, out index);
    var primaryCategory = NormalizeToken(index?.PrimaryCategory ?? "Unknown", "unknown");

    return new ExportRecord
    {
        Sequence = sequence,
        DocumentId = documentId,
        Title = title ?? "Unknown",
        HebrewTitle = index?.HebrewTitle,
        Language = language,
        VersionTitle = versionTitle ?? "default",
        Reference = reference ?? $"sequence-{sequence}",
        Categories = index?.Categories ?? new List<string>(),
        PrimaryCategory = primaryCategory
    };
}

static string BuildTargetFilePath(string outputRoot, ExportRecord record)
{
    var categorySegment = Slugify(record.PrimaryCategory, 80);
    var titleSegment = Slugify(record.Title, 120);
    var languageSegment = Slugify(record.Language, 32);
    var versionSegment = Slugify(record.VersionTitle, 120);
    var referenceSegment = Slugify(record.Reference, 140);
    var stableHash = ComputeStableHash($"{record.DocumentId}|{record.Reference}");

    return Path.Combine(
        outputRoot,
        "texts",
        categorySegment,
        titleSegment,
        languageSegment,
        versionSegment,
        $"{referenceSegment}__{stableHash}.json");
}

static async Task WriteExportFileAsync(string path, ExportRecord record, BsonDocument sourceDocument)
{
    var payload = new ExportPayload
    {
        SchemaVersion = 1,
        ExportedAtUtc = DateTime.UtcNow,
        Sequence = record.Sequence,
        DocumentId = record.DocumentId,
        Title = record.Title,
        HebrewTitle = record.HebrewTitle,
        Language = record.Language,
        VersionTitle = record.VersionTitle,
        Reference = record.Reference,
        PrimaryCategory = record.PrimaryCategory,
        Categories = record.Categories,
        Source = JsonDocument.Parse(
            sourceDocument.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson })).RootElement.Clone()
    };

    var tempPath = $"{path}.tmp";
    await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, JsonSettings.Pretty));
    File.Move(tempPath, path, overwrite: true);
}

static string ReadObjectIdAsString(BsonDocument doc, string field)
{
    return doc.TryGetValue(field, out var idValue)
        ? idValue.ToString() ?? string.Empty
        : string.Empty;
}

static string? ReadString(BsonDocument doc, params string[] fields)
{
    foreach (var field in fields)
    {
        if (!doc.TryGetValue(field, out var value))
        {
            continue;
        }

        if (value.IsString)
        {
            var text = value.AsString.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
    }

    return null;
}

static List<string> ReadStringArray(BsonDocument doc, string field)
{
    if (!doc.TryGetValue(field, out var value) || value.BsonType != BsonType.Array)
    {
        return new List<string>();
    }

    var items = new List<string>();
    foreach (var element in value.AsBsonArray)
    {
        if (!element.IsString)
        {
            continue;
        }

        var text = element.AsString.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(text);
        }
    }

    return items;
}

static string NormalizeToken(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static string Slugify(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "unknown";
    }

    var candidate = value.Trim().ToLowerInvariant();
    var builder = new StringBuilder(candidate.Length);
    foreach (var c in candidate)
    {
        if (char.IsLetterOrDigit(c))
        {
            builder.Append(c);
        }
        else if (c is ' ' or '-' or '_' or '.')
        {
            builder.Append('-');
        }
    }

    var collapsed = builder.ToString().Trim('-');
    while (collapsed.Contains("--", StringComparison.Ordinal))
    {
        collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
    }

    if (collapsed.Length == 0)
    {
        return "unknown";
    }

    return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength];
}

static string ComputeStableHash(string input)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(bytes[..6]).ToLowerInvariant();
}

static class JsonSettings
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

sealed class CliOptions
{
    public required string DumpPath { get; init; }
    public required string OutputRoot { get; init; }
    public string? CheckpointPath { get; init; }
    public bool Overwrite { get; init; }
    public int CheckpointEvery { get; init; } = 1000;
    public int MaxDocuments { get; init; }
    public bool ShowHelp { get; init; }

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
        {
            return new CliOptions
            {
                DumpPath = string.Empty,
                OutputRoot = string.Empty,
                ShowHelp = true
            };
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            if (key is "overwrite")
            {
                values[key] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{key}");
            }

            values[key] = args[++i];
        }

        if (!values.TryGetValue("dump", out var dumpPath) || string.IsNullOrWhiteSpace(dumpPath))
        {
            throw new ArgumentException("Missing required argument --dump");
        }

        if (!values.TryGetValue("output", out var outputRoot) || string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Missing required argument --output");
        }

        var checkpointEvery = 1000;
        if (values.TryGetValue("checkpoint-every", out var checkpointEveryRaw) &&
            (!int.TryParse(checkpointEveryRaw, out checkpointEvery) || checkpointEvery <= 0))
        {
            throw new ArgumentException("--checkpoint-every must be a positive integer");
        }

        var maxDocs = 0;
        if (values.TryGetValue("max-docs", out var maxDocsRaw) &&
            (!int.TryParse(maxDocsRaw, out maxDocs) || maxDocs < 0))
        {
            throw new ArgumentException("--max-docs must be 0 or a positive integer");
        }

        return new CliOptions
        {
            DumpPath = Path.GetFullPath(dumpPath),
            OutputRoot = Path.GetFullPath(outputRoot),
            CheckpointPath = values.TryGetValue("checkpoint", out var checkpointPath)
                ? Path.GetFullPath(checkpointPath)
                : null,
            Overwrite = values.TryGetValue("overwrite", out var overwriteRaw) &&
                        string.Equals(overwriteRaw, "true", StringComparison.OrdinalIgnoreCase),
            CheckpointEvery = checkpointEvery,
            MaxDocuments = maxDocs
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
Usage:
  dotnet run --project .\Tools\SefariaDumpExtractor\SefariaDumpExtractor.csproj -- \
    --dump <path-to-sefaria-dump.tar.gz> \
    --output <output-folder> \
    [--checkpoint <checkpoint-file>] \
    [--checkpoint-every <n>] \
    [--overwrite] \
    [--max-docs <n>]

Notes:
  - The extractor streams BSON directly from the tar.gz archive.
  - Resume is driven by checkpoint sequence + deterministic output file paths.
  - Existing files are skipped by default unless --overwrite is set.
""");
    }
}

sealed class ExtractionProgress
{
    public long LastSequenceProcessed { get; set; }

    public static ExtractionProgress Load(string checkpointPath, string dumpPath)
    {
        if (!File.Exists(checkpointPath))
        {
            return new ExtractionProgress();
        }

        var checkpoint = JsonSerializer.Deserialize<CheckpointDocument>(File.ReadAllText(checkpointPath), JsonSettings.Pretty);
        if (checkpoint is null ||
            !string.Equals(Path.GetFullPath(checkpoint.DumpPath), Path.GetFullPath(dumpPath), StringComparison.OrdinalIgnoreCase))
        {
            return new ExtractionProgress();
        }

        return new ExtractionProgress
        {
            LastSequenceProcessed = checkpoint.LastSequenceProcessed
        };
    }

    public void Save(string checkpointPath, string dumpPath, bool completed)
    {
        var directory = Path.GetDirectoryName(checkpointPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new CheckpointDocument
        {
            DumpPath = Path.GetFullPath(dumpPath),
            LastSequenceProcessed = LastSequenceProcessed,
            UpdatedAtUtc = DateTime.UtcNow,
            Completed = completed
        };
        File.WriteAllText(checkpointPath, JsonSerializer.Serialize(document, JsonSettings.Pretty));
    }
}

sealed class NdjsonMetadataSink : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _runId;

    public NdjsonMetadataSink(string path, string runId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        _runId = runId;
    }

    public async Task WriteAsync(ExportRecord record)
    {
        var envelope = new
        {
            runId = _runId,
            sequence = record.Sequence,
            documentId = record.DocumentId,
            title = record.Title,
            language = record.Language,
            versionTitle = record.VersionTitle,
            reference = record.Reference,
            primaryCategory = record.PrimaryCategory,
            categories = record.Categories,
            relativePath = record.RelativePath
        };
        await _writer.WriteLineAsync(JsonSerializer.Serialize(envelope));
        await _writer.FlushAsync();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}

sealed class CheckpointDocument
{
    public string DumpPath { get; set; } = string.Empty;
    public long LastSequenceProcessed { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool Completed { get; set; }
}

sealed class IndexMetadata(string title, string? hebrewTitle, List<string> categories, string primaryCategory)
{
    public string Title { get; } = title;
    public string? HebrewTitle { get; } = hebrewTitle;
    public List<string> Categories { get; } = categories;
    public string PrimaryCategory { get; } = primaryCategory;
}

sealed class ExportRecord
{
    public long Sequence { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? HebrewTitle { get; set; }
    public string Language { get; set; } = "unknown";
    public string VersionTitle { get; set; } = "default";
    public string Reference { get; set; } = string.Empty;
    public string PrimaryCategory { get; set; } = "unknown";
    public List<string> Categories { get; set; } = new();
    public string RelativePath { get; set; } = string.Empty;
}

sealed class ExportPayload
{
    public int SchemaVersion { get; set; }
    public DateTime ExportedAtUtc { get; set; }
    public long Sequence { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? HebrewTitle { get; set; }
    public string Language { get; set; } = "unknown";
    public string VersionTitle { get; set; } = "default";
    public string Reference { get; set; } = string.Empty;
    public string PrimaryCategory { get; set; } = "unknown";
    public List<string> Categories { get; set; } = new();
    public JsonElement Source { get; set; }
}

sealed class ExtractionSummary
{
    public string RunId { get; set; } = string.Empty;
    public string DumpPath { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public double DocsPerSecond { get; set; }
    public long ResumedFromSequence { get; set; }
    public long LastSequenceProcessed { get; set; }
    public long IndexDocumentsRead { get; set; }
    public long TotalTextDocumentsSeen { get; set; }
    public long ExportedDocuments { get; set; }
    public long SkippedByCheckpoint { get; set; }
    public long SkippedExistingFiles { get; set; }
}

sealed class TarEntryReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable[] _disposables;

    public TarEntryReadStream(Stream inner, params IDisposable[] disposables)
    {
        _inner = inner;
        _disposables = disposables;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        _inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        return _inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
