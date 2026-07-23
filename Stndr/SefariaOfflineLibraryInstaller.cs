using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Stndr;

public sealed class SefariaOfflineLibraryInstaller
{
    private const long MinimumImportFreeBytes = 5L * 1024 * 1024 * 1024;
    private const long MinimumDownloadAndImportFreeBytes = 8L * 1024 * 1024 * 1024;
    private readonly SefariaArchiveDownloader _downloader;
    private readonly SefariaOfflineLibraryImporter _importer;
    private readonly ISefariaLibraryUpdateSource _updateSource;

    public SefariaOfflineLibraryInstaller(
        SefariaArchiveDownloader? downloader = null,
        SefariaOfflineLibraryImporter? importer = null,
        ISefariaLibraryUpdateSource? updateSource = null)
    {
        _downloader = downloader ?? new();
        _importer = importer ?? new();
        _updateSource = updateSource ?? new SefariaSnapshotUpdateSource();
    }

    public static bool IsInstalled(string dataFolder) => File.Exists(SefariaOfflineLibraryPaths.ActiveDatabase(dataFolder)) &&
        File.Exists(SefariaOfflineLibraryPaths.InstallMetadata(dataFolder));

    public async Task<SefariaOfflineLibraryImportResult> DownloadAndInstallAsync(
        string dataFolder,
        IProgress<SefariaOfflineLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureFreeSpace(dataFolder, MinimumDownloadAndImportFreeBytes);
        var snapshot = await _updateSource.GetLatestAsync(cancellationToken);
        var archive = await _downloader.DownloadAsync(
            snapshot.SourceUri,
            SefariaOfflineLibraryPaths.DownloadedArchive(dataFolder),
            progress,
            cancellationToken);
        return await InstallCoreAsync(dataFolder, archive, snapshot, progress, cancellationToken);
    }

    public async Task<SefariaOfflineLibraryImportResult> InstallAsync(
        string dataFolder,
        string archivePath,
        IProgress<SefariaOfflineLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await InstallCoreAsync(dataFolder, archivePath, null, progress, cancellationToken);

    private async Task<SefariaOfflineLibraryImportResult> InstallCoreAsync(
        string dataFolder,
        string archivePath,
        SefariaRemoteSnapshot? snapshot,
        IProgress<SefariaOfflineLibraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureFreeSpace(dataFolder, MinimumImportFreeBytes);
        var databaseFolder = SefariaOfflineLibraryPaths.DatabaseFolder(dataFolder);
        Directory.CreateDirectory(databaseFolder);
        var staging = SefariaOfflineLibraryPaths.StagingDatabase(dataFolder);
        var active = SefariaOfflineLibraryPaths.ActiveDatabase(dataFolder);
        var previous = SefariaOfflineLibraryPaths.PreviousDatabase(dataFolder);

        var result = await _importer.ImportAsync(archivePath, staging, progress, cancellationToken);
        progress?.Report(new(SefariaOfflineLibraryStage.Validating, "Checking the completed offline library"));
        await ValidateAsync(staging, result, cancellationToken);

        var archiveHash = await HashFileAsync(archivePath, cancellationToken);
        var metadata = new SefariaOfflineLibraryInstallMetadata
        {
            SchemaVersion = SefariaOfflineLibraryImporter.SchemaVersion,
            InstalledAtUtc = DateTime.UtcNow,
            SourceArchive = Path.GetFullPath(archivePath),
            SourceArchiveBytes = new FileInfo(archivePath).Length,
            SourceArchiveSha256 = Convert.ToHexString(archiveHash).ToLowerInvariant(),
            SourceRemoteUrl = snapshot?.SourceUri.ToString() ?? string.Empty,
            SourceRemoteETag = snapshot?.ETag ?? string.Empty,
            SourceRemoteLastModifiedUtc = snapshot?.LastModifiedUtc,
            SourceRemoteBytes = snapshot?.ContentLength ?? 0,
            Works = result.Works,
            Versions = result.Versions,
            Links = result.Links,
            LinkEndpoints = result.LinkEndpoints,
            UniqueReferences = result.UniqueReferences,
            Segments = result.Segments,
            Lexicons = result.Lexicons,
            LexiconEntries = result.LexiconEntries,
            DictionaryWordForms = result.DictionaryWordForms,
            DatabaseBytes = result.DatabaseBytes
        };

        progress?.Report(new(SefariaOfflineLibraryStage.Installing, "Activating the new offline library"));
        await ActivateDatabaseAsync(staging, active, previous, cancellationToken);

        var metadataPath = SefariaOfflineLibraryPaths.InstallMetadata(dataFolder);
        var temporaryMetadata = metadataPath + ".tmp";
        await using (var output = File.Create(temporaryMetadata))
            await JsonSerializer.SerializeAsync(output, metadata, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        File.Move(temporaryMetadata, metadataPath, true);
        progress?.Report(new(SefariaOfflineLibraryStage.Complete,
            $"Offline library installed: {result.Works:N0} books, {result.Versions:N0} versions, " +
            $"{result.Links:N0} links and {result.LexiconEntries:N0} dictionary entries"));
        return result;
    }

    private static async Task ActivateDatabaseAsync(
        string staging,
        string active,
        string previous,
        CancellationToken cancellationToken)
    {
        const int attempts = 5;
        IOException? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(active))
                {
                    SefariaOfflineLibraryImporter.DeleteDatabaseFiles(previous);
                    File.Replace(staging, active, previous, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(staging, active);
                }
                return;
            }
            catch (IOException exception)
            {
                lastError = exception;
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        throw new IOException("The offline library database could not be activated after releasing SQLite file handles.", lastError);
    }

    private static async Task ValidateAsync(string databasePath, SefariaOfflineLibraryImportResult expected, CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(token);
        var integrity = await ScalarStringAsync(connection, "PRAGMA integrity_check", token);
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SQLite integrity check failed: {integrity}");
        await RequireCountAsync(connection, "works", expected.Works, token);
        await RequireCountAsync(connection, "versions", expected.Versions, token);
        await RequireCountAsync(connection, "links", expected.Links, token);
        await RequireCountAsync(connection, "link_endpoints", expected.LinkEndpoints, token);
        await RequireCountAsync(connection, "refs", expected.UniqueReferences, token);
        await RequireCountAsync(connection, "lexicons", expected.Lexicons, token);
        await RequireCountAsync(connection, "lexicon_entries", expected.LexiconEntries, token);
        await RequireCountAsync(connection, "dictionary_word_forms", expected.DictionaryWordForms, token);
    }

    private static async Task RequireCountAsync(SqliteConnection connection, string table, long expected, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var actual = Convert.ToInt64(await command.ExecuteScalarAsync(token));
        if (actual != expected) throw new InvalidDataException($"Validation of {table} failed: expected {expected:N0}, found {actual:N0}.");
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(token)) ?? "";
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return await sha.ComputeHashAsync(stream, token);
    }

    private static void EnsureFreeSpace(string dataFolder, long required)
    {
        Directory.CreateDirectory(dataFolder);
        var root = Path.GetPathRoot(Path.GetFullPath(dataFolder));
        if (string.IsNullOrWhiteSpace(root)) return;
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"The selected drive has {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:N1} GiB free. " +
                                  $"At least {required / 1024d / 1024d / 1024d:N0} GiB is needed for this operation.");
    }
}
