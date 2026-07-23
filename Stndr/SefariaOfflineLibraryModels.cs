using System;
using System.IO;

namespace Stndr;

public static class SefariaOfflineLibraryPaths
{
    public const string DefaultArchiveUrl =
        "https://storage.googleapis.com/sefaria-mongo-backup/dump_small.tar.gz";

    public static string DatabaseFolder(string dataFolder) => Path.Combine(dataFolder, "database");
    public static string ActiveDatabase(string dataFolder) => Path.Combine(DatabaseFolder(dataFolder), "sefaria-library.sqlite");
    public static string StagingDatabase(string dataFolder) => Path.Combine(DatabaseFolder(dataFolder), "sefaria-library-importing.sqlite");
    public static string PreviousDatabase(string dataFolder) => Path.Combine(DatabaseFolder(dataFolder), "sefaria-library-previous.sqlite");
    public static string ArchiveFolder(string dataFolder) => Path.Combine(dataFolder, "downloads");
    public static string DownloadedArchive(string dataFolder) => Path.Combine(ArchiveFolder(dataFolder), "sefaria-dump-small.tar.gz");
    public static string PartialArchive(string dataFolder) => DownloadedArchive(dataFolder) + ".part";
    public static string DownloadState(string dataFolder) => DownloadedArchive(dataFolder) + ".download.json";
    public static string InstallMetadata(string dataFolder) => Path.Combine(DatabaseFolder(dataFolder), "sefaria-library.json");
}

public enum SefariaOfflineLibraryStage
{
    Downloading,
    ReadingArchive,
    ImportingMetadata,
    ImportingDictionaries,
    ImportingLinks,
    ImportingTexts,
    BuildingIndexes,
    Validating,
    Installing,
    Complete
}

public sealed record SefariaOfflineLibraryProgress(
    SefariaOfflineLibraryStage Stage,
    string Message,
    long Completed = 0,
    long? Total = null)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Completed / Total.Value, 0, 1) : null;
}

public sealed record SefariaOfflineLibraryImportResult(
    int Works,
    int Versions,
    long Links,
    long LinkEndpoints,
    int UniqueReferences,
    long Segments,
    int Lexicons,
    int LexiconEntries,
    int DictionaryWordForms,
    long DatabaseBytes,
    TimeSpan Elapsed);

public sealed class SefariaOfflineLibraryInstallMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime InstalledAtUtc { get; set; }
    public string SourceArchive { get; set; } = string.Empty;
    public long SourceArchiveBytes { get; set; }
    public string SourceArchiveSha256 { get; set; } = string.Empty;
    public string SourceRemoteUrl { get; set; } = string.Empty;
    public string SourceRemoteETag { get; set; } = string.Empty;
    public DateTimeOffset? SourceRemoteLastModifiedUtc { get; set; }
    public long SourceRemoteBytes { get; set; }
    public int Works { get; set; }
    public int Versions { get; set; }
    public long Links { get; set; }
    public long LinkEndpoints { get; set; }
    public int UniqueReferences { get; set; }
    public long Segments { get; set; }
    public int Lexicons { get; set; }
    public int LexiconEntries { get; set; }
    public int DictionaryWordForms { get; set; }
    public long DatabaseBytes { get; set; }
}
