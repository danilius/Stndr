using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed class SefariaArchiveDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<string> DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<SefariaOfflineLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partialPath = destinationPath + ".part";
        var statePath = destinationPath + ".download.json";
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        var state = await ReadStateAsync(statePath, cancellationToken);

        if (existingLength > 0 &&
            state?.ExpectedBytes == existingLength &&
            string.Equals(state.Source, source.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new(
                SefariaOfflineLibraryStage.Downloading,
                $"Using completed Sefaria download ({FormatBytes(existingLength)})",
                existingLength,
                existingLength));
            File.Move(partialPath, destinationPath, true);
            File.Delete(statePath);
            return destinationPath;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
            if (!string.IsNullOrWhiteSpace(state?.ETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", state.ETag);
            }
            else if (state?.LastModifiedUtc is { } lastModified)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var resumed = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            existingLength = 0;
        }

        var responseLength = response.Content.Headers.ContentLength;
        var totalLength = response.Content.Headers.ContentRange?.Length ??
            (responseLength is null ? null : existingLength + responseLength.Value);
        var newState = new DownloadState
        {
            Source = source.ToString(),
            ETag = response.Headers.ETag?.ToString(),
            LastModifiedUtc = response.Content.Headers.LastModified,
            ExpectedBytes = totalLength
        };
        await WriteStateAsync(statePath, newState, cancellationToken);

        long completed;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
                         partialPath,
                         resumed ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            completed = existingLength;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                completed += read;
                progress?.Report(new(
                    SefariaOfflineLibraryStage.Downloading,
                    $"Downloading Sefaria library ({FormatBytes(completed)} of {FormatBytes(totalLength)})",
                    completed,
                    totalLength));
            }
            await output.FlushAsync(cancellationToken);
        }

        if (totalLength is not null && completed != totalLength)
        {
            throw new InvalidDataException($"The download ended at {completed:N0} bytes; {totalLength:N0} were expected.");
        }

        File.Move(partialPath, destinationPath, true);
        File.Delete(statePath);
        return destinationPath;
    }

    private static string FormatBytes(long? bytes) => bytes is null
        ? "unknown size"
        : $"{bytes.Value / 1024d / 1024d / 1024d:N2} GiB";

    private static async Task<DownloadState?> ReadStateAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DownloadState>(stream, cancellationToken: token);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteStateAsync(string path, DownloadState state, CancellationToken token)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, cancellationToken: token);
    }

    private sealed class DownloadState
    {
        public string Source { get; set; } = string.Empty;
        public string? ETag { get; set; }
        public DateTimeOffset? LastModifiedUtc { get; set; }
        public long? ExpectedBytes { get; set; }
    }
}
