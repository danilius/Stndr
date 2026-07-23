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
    long? ContentLength);

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
            return CreateSnapshot(uri, response);

        if (response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotImplemented)
            response.EnsureSuccessStatusCode();

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
    Error
}

public sealed record SefariaLibraryUpdateState(
    SefariaLibraryUpdateMode Mode,
    string Message,
    SefariaRemoteSnapshot? RemoteSnapshot = null);

public sealed class SefariaLibraryUpdateService(ISefariaLibraryUpdateSource? source = null) : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private readonly ISefariaLibraryUpdateSource _source = source ?? new SefariaSnapshotUpdateSource();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _dismissed;
    private bool _backgroundStarted;

    public SefariaLibraryUpdateState CurrentState { get; private set; } =
        new(SefariaLibraryUpdateMode.Hidden, "");
    public event Action<SefariaLibraryUpdateState>? StateChanged;

    public void StartBackgroundChecks(Func<string?> dataFolderProvider, Func<bool>? enabledProvider = null)
    {
        if (_backgroundStarted) return;
        _backgroundStarted = true;
        _ = RunBackgroundChecksAsync(dataFolderProvider, enabledProvider ?? (() => true), _lifetime.Token);
    }

    public void DismissBanner()
    {
        _dismissed = true;
        StateChanged?.Invoke(CurrentState with { Mode = SefariaLibraryUpdateMode.Hidden });
    }

    public async Task<SefariaLibraryUpdateState> CheckNowAsync(string? dataFolder, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(dataFolder) || !SefariaOfflineLibraryInstaller.IsInstalled(dataFolder))
            return Publish(new(SefariaLibraryUpdateMode.Hidden, ""));

        Publish(new(SefariaLibraryUpdateMode.Checking, "Checking for Sefaria library updates..."));
        try
        {
            var remote = await _source.GetLatestAsync(token);
            var local = await ReadMetadataAsync(dataFolder, token);
            var available = IsNewer(local, remote);
            _dismissed = false;
            return Publish(available
                ? new(SefariaLibraryUpdateMode.UpdateAvailable,
                    $"A Sefaria library update is available ({FormatSize(remote.ContentLength)}).", remote)
                : new(SefariaLibraryUpdateMode.UpToDate,
                    $"The Sefaria library is up to date. Installed {local?.InstalledAtUtc.ToLocalTime():d MMM yyyy}.", remote));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Publish(new(SefariaLibraryUpdateMode.Error, $"Could not check the Sefaria library: {ex.Message}"));
        }
    }

    private async Task RunBackgroundChecksAsync(Func<string?> provider, Func<bool> enabled, CancellationToken token)
    {
        try
        {
            await Task.Delay(InitialDelay, token);
            while (!token.IsCancellationRequested)
            {
                if (enabled()) await CheckNowAsync(provider(), token);
                await Task.Delay(CheckInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private SefariaLibraryUpdateState Publish(SefariaLibraryUpdateState state)
    {
        CurrentState = state;
        if (!_dismissed || state.Mode != SefariaLibraryUpdateMode.UpdateAvailable) StateChanged?.Invoke(state);
        return state;
    }

    private static bool IsNewer(SefariaOfflineLibraryInstallMetadata? local, SefariaRemoteSnapshot remote)
    {
        if (local is null) return false;
        if (!string.IsNullOrWhiteSpace(local.SourceRemoteETag) && !string.IsNullOrWhiteSpace(remote.ETag))
            return !string.Equals(local.SourceRemoteETag, remote.ETag, StringComparison.Ordinal);
        if (local.SourceRemoteLastModifiedUtc is { } known && remote.LastModifiedUtc is { } latest)
            return latest > known.AddSeconds(1);
        if (remote.LastModifiedUtc is { } modified && modified.UtcDateTime > local.InstalledAtUtc.AddMinutes(1))
            return true;
        return local.SourceRemoteBytes > 0 && remote.ContentLength is { } bytes && bytes != local.SourceRemoteBytes;
    }

    private static async Task<SefariaOfflineLibraryInstallMetadata?> ReadMetadataAsync(string folder, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(SefariaOfflineLibraryPaths.InstallMetadata(folder));
            return await JsonSerializer.DeserializeAsync<SefariaOfflineLibraryInstallMetadata>(stream, cancellationToken: token);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static string FormatSize(long? bytes) => bytes is null ? "size unknown" : $"{bytes.Value / 1024d / 1024d / 1024d:N2} GiB";
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
