using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed record SefariaRemoteSnapshot(
    Uri SourceUri,
    string ETag,
    DateTimeOffset? LastModifiedUtc,
    long? ContentLength)
{
    public string IdentityKey
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ETag))
            {
                return "etag:" + ETag;
            }

            if (LastModifiedUtc is { } modified)
            {
                return "lm:" + modified.UtcTicks;
            }

            if (ContentLength is { } bytes)
            {
                return "len:" + bytes;
            }

            return "unknown";
        }
    }
}

public interface ISefariaLibraryUpdateSource
{
    Task<SefariaRemoteSnapshot> GetLatestAsync(CancellationToken cancellationToken = default);
}

public sealed class SefariaSnapshotUpdateSource(HttpClient? httpClient = null) : ISefariaLibraryUpdateSource
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<SefariaRemoteSnapshot> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(SefariaOfflineLibraryPaths.DefaultArchiveUrl);
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return CreateSnapshot(uri, response);
        }

        if (response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotImplemented)
        {
            response.EnsureSuccessStatusCode();
        }

        using var range = new HttpRequestMessage(HttpMethod.Get, uri);
        range.Headers.Range = new RangeHeaderValue(0, 0);
        using var rangeResponse = await _httpClient.SendAsync(range, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        rangeResponse.EnsureSuccessStatusCode();
        return CreateSnapshot(uri, rangeResponse);
    }

    private static SefariaRemoteSnapshot CreateSnapshot(Uri uri, HttpResponseMessage response) => new(
        uri,
        response.Headers.ETag?.ToString() ?? "",
        response.Content.Headers.LastModified,
        response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength);
}

public enum SefariaLibraryUpdateMode
{
    Hidden,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Importing,
    Activating,
    Complete,
    Cancelled,
    Error
}

public sealed record SefariaLibraryUpdateState(
    SefariaLibraryUpdateMode Mode,
    string Message,
    SefariaRemoteSnapshot? RemoteSnapshot = null,
    double? ProgressFraction = null,
    bool CanCancel = false);

public sealed class SefariaLibraryUpdateService(ISefariaLibraryUpdateSource? source = null) : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private readonly ISefariaLibraryUpdateSource _source = source ?? new SefariaSnapshotUpdateSource();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _backgroundStarted;

    public SefariaLibraryUpdateState CurrentState { get; private set; } =
        new(SefariaLibraryUpdateMode.Hidden, "");
    public event Action<SefariaLibraryUpdateState>? StateChanged;

    public bool IsBusy => CurrentState.Mode is
        SefariaLibraryUpdateMode.Downloading or
        SefariaLibraryUpdateMode.Importing or
        SefariaLibraryUpdateMode.Activating;

    public void StartBackgroundChecks(
        Func<string?> dataFolderProvider,
        Func<bool>? enabledProvider = null,
        Func<LibraryUpdateSnoozeState>? snoozeProvider = null)
    {
        if (_backgroundStarted)
        {
            return;
        }

        _backgroundStarted = true;
        _ = RunBackgroundChecksAsync(
            dataFolderProvider,
            enabledProvider ?? (() => true),
            snoozeProvider ?? (() => LibraryUpdateSnoozeState.None),
            _lifetime.Token);
    }

    public async Task<SefariaLibraryUpdateState> CheckNowAsync(
        string? dataFolder,
        LibraryUpdateSnoozeState snooze = default,
        CancellationToken token = default)
    {
        if (IsBusy)
        {
            return CurrentState;
        }

        if (string.IsNullOrWhiteSpace(dataFolder) || !SefariaOfflineLibraryInstaller.IsInstalled(dataFolder))
        {
            return Publish(new(SefariaLibraryUpdateMode.Hidden, ""));
        }

        Publish(new(SefariaLibraryUpdateMode.Checking, "Checking for Sefaria library updates..."));
        try
        {
            var remote = await _source.GetLatestAsync(token);
            var local = await ReadMetadataAsync(dataFolder, token);
            var available = IsNewer(local, remote);
            if (!available)
            {
                return Publish(new(
                    SefariaLibraryUpdateMode.UpToDate,
                    $"The Sefaria library is up to date. Installed {local?.InstalledAtUtc.ToLocalTime():d MMM yyyy}.",
                    remote));
            }

            if (IsSnoozed(remote, snooze))
            {
                return Publish(new(
                    SefariaLibraryUpdateMode.Hidden,
                    $"A Sefaria library update is snoozed until {snooze.UntilUtc?.ToLocalTime():d MMM yyyy}.",
                    remote));
            }

            return Publish(new(
                SefariaLibraryUpdateMode.UpdateAvailable,
                $"A Sefaria library update is available ({FormatSize(remote.ContentLength)}). Updating replaces the full offline snapshot.",
                remote));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Publish(new(SefariaLibraryUpdateMode.Error, $"Could not check the Sefaria library: {ex.Message}"));
        }
    }

    public void BeginProgress(SefariaLibraryUpdateMode mode, string message, SefariaRemoteSnapshot? remote = null)
    {
        Publish(new(mode, message, remote ?? CurrentState.RemoteSnapshot, ProgressFraction: null, CanCancel: mode != SefariaLibraryUpdateMode.Activating));
    }

    public void ReportProgress(SefariaOfflineLibraryProgress progress, SefariaRemoteSnapshot? remote = null)
    {
        var mode = progress.Stage switch
        {
            SefariaOfflineLibraryStage.Downloading => SefariaLibraryUpdateMode.Downloading,
            SefariaOfflineLibraryStage.Installing => SefariaLibraryUpdateMode.Activating,
            SefariaOfflineLibraryStage.Complete => SefariaLibraryUpdateMode.Complete,
            _ => SefariaLibraryUpdateMode.Importing
        };
        var canCancel = mode is SefariaLibraryUpdateMode.Downloading or SefariaLibraryUpdateMode.Importing;
        Publish(new(mode, progress.Message, remote ?? CurrentState.RemoteSnapshot, progress.Fraction, canCancel));
    }

    public void Complete(string message, SefariaRemoteSnapshot? remote = null) =>
        Publish(new(SefariaLibraryUpdateMode.Complete, message, remote ?? CurrentState.RemoteSnapshot));

    public void Cancelled(string message) =>
        Publish(new(SefariaLibraryUpdateMode.Cancelled, message, CurrentState.RemoteSnapshot));

    public void Fail(string message) =>
        Publish(new(SefariaLibraryUpdateMode.Error, message, CurrentState.RemoteSnapshot));

    public void Hide() => Publish(new(SefariaLibraryUpdateMode.Hidden, "", CurrentState.RemoteSnapshot));

    private async Task RunBackgroundChecksAsync(
        Func<string?> provider,
        Func<bool> enabled,
        Func<LibraryUpdateSnoozeState> snoozeProvider,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(InitialDelay, token);
            while (!token.IsCancellationRequested)
            {
                if (enabled() && !IsBusy)
                {
                    await CheckNowAsync(provider(), snoozeProvider(), token);
                }

                await Task.Delay(CheckInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private SefariaLibraryUpdateState Publish(SefariaLibraryUpdateState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
        return state;
    }

    internal static bool IsNewer(SefariaOfflineLibraryInstallMetadata? local, SefariaRemoteSnapshot remote)
    {
        if (local is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(local.SourceRemoteETag) && !string.IsNullOrWhiteSpace(remote.ETag))
        {
            return !string.Equals(local.SourceRemoteETag, remote.ETag, StringComparison.Ordinal);
        }

        if (local.SourceRemoteLastModifiedUtc is { } known && remote.LastModifiedUtc is { } latest)
        {
            return latest > known.AddSeconds(1);
        }

        if (local.SourceRemoteBytes > 0 && remote.ContentLength is { } remoteBytesFromProvenance)
        {
            return remoteBytesFromProvenance != local.SourceRemoteBytes;
        }

        // Local-archive installs often lack remote provenance. Only compare archive size when we have one.
        if (string.IsNullOrWhiteSpace(local.SourceRemoteETag) &&
            local.SourceRemoteLastModifiedUtc is null &&
            local.SourceArchiveBytes > 0 &&
            remote.ContentLength is { } remoteBytes)
        {
            return remoteBytes != local.SourceArchiveBytes;
        }

        return false;
    }

    private static bool IsSnoozed(SefariaRemoteSnapshot remote, LibraryUpdateSnoozeState snooze)
    {
        if (string.IsNullOrWhiteSpace(snooze.RemoteKey) || snooze.UntilUtc is null)
        {
            return false;
        }

        if (DateTime.UtcNow >= snooze.UntilUtc.Value)
        {
            return false;
        }

        return string.Equals(snooze.RemoteKey, remote.IdentityKey, StringComparison.Ordinal);
    }

    private static async Task<SefariaOfflineLibraryInstallMetadata?> ReadMetadataAsync(string folder, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(SefariaOfflineLibraryPaths.InstallMetadata(folder));
            return await JsonSerializer.DeserializeAsync<SefariaOfflineLibraryInstallMetadata>(stream, cancellationToken: token);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string FormatSize(long? bytes) =>
        bytes is null ? "size unknown" : $"{bytes.Value / 1024d / 1024d / 1024d:N2} GiB";

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public readonly record struct LibraryUpdateSnoozeState(string? RemoteKey, DateTime? UntilUtc)
{
    public static LibraryUpdateSnoozeState None => default;
}
