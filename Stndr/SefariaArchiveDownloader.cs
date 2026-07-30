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

    /// <summary>
    /// Downloads <paramref name="source"/> to <paramref name="destinationPath"/>, resuming from
    /// <c>.part</c> when present. If the destination already holds a complete file, skips the network.
    /// </summary>
    /// <param name="expectedContentLength">
    /// Optional remote size (e.g. from HEAD). Used to recognize a finished local file and to validate partials.
    /// </param>
    public async Task<string> DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<SefariaOfflineLibraryProgress>? progress = null,
        CancellationToken cancellationToken = default,
        long? expectedContentLength = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partialPath = destinationPath + ".part";
        var statePath = destinationPath + ".download.json";
        var state = await ReadStateAsync(statePath, cancellationToken);
        var expectedTotal = expectedContentLength ?? state?.ExpectedBytes;

        // Finished archive left after a previous download (install was interrupted): reuse it.
        if (File.Exists(destinationPath))
        {
            var existingComplete = new FileInfo(destinationPath).Length;
            if (IsCompleteLength(existingComplete, expectedTotal, state, source))
            {
                progress?.Report(new(
                    SefariaOfflineLibraryStage.Downloading,
                    $"Using completed Sefaria download ({FormatBytes(existingComplete)})",
                    existingComplete,
                    existingComplete));
                TryDeleteFile(partialPath);
                TryDeleteFile(statePath);
                return destinationPath;
            }

            // Wrong size or unknown — remove and re-download.
            TryDeleteFile(destinationPath);
        }

        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        // Partial already holds the full object (download finished, rename interrupted).
        if (existingLength > 0 &&
            IsCompleteLength(existingLength, expectedTotal, state, source) &&
            SourceMatches(state, source))
        {
            progress?.Report(new(
                SefariaOfflineLibraryStage.Downloading,
                $"Using completed Sefaria download ({FormatBytes(existingLength)})",
                existingLength,
                existingLength));
            File.Move(partialPath, destinationPath, true);
            TryDeleteFile(statePath);
            return destinationPath;
        }

        // Partial larger than known total is corrupt.
        if (existingLength > 0 && expectedTotal is { } knownTotal && existingLength > knownTotal)
        {
            TryDeleteFile(partialPath);
            existingLength = 0;
        }

        // Source changed since the partial was written — start over.
        if (existingLength > 0 && state is not null && !SourceMatches(state, source))
        {
            TryDeleteFile(partialPath);
            existingLength = 0;
            state = null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        // Prefer Range without If-Range so a flaky ETag/Last-Modified mismatch does not force a
        // multi-GiB restart. If the object truly changed, Content-Range / length checks below restart.
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server says our offset is invalid (often: partial already complete, or file shrank).
            TryDeleteFile(partialPath);
            existingLength = 0;
            return await DownloadAsync(source, destinationPath, progress, cancellationToken, expectedContentLength);
        }

        response.EnsureSuccessStatusCode();

        var contentRange = response.Content.Headers.ContentRange;
        var responseLength = response.Content.Headers.ContentLength;
        long resumeFrom;
        bool append;

        if (existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            // Standard resume.
            var from = contentRange?.From ?? existingLength;
            if (from != existingLength)
            {
                // Server started at an unexpected offset — restart cleanly.
                TryDeleteFile(partialPath);
                existingLength = 0;
                return await DownloadAsync(source, destinationPath, progress, cancellationToken, expectedContentLength);
            }

            resumeFrom = existingLength;
            append = true;
        }
        else if (existingLength > 0 &&
                 response.StatusCode == HttpStatusCode.OK &&
                 responseLength is { } bodyLen &&
                 bodyLen == existingLength &&
                 expectedTotal is { } total &&
                 existingLength == total)
        {
            // Edge case: some servers ignore Range and return empty/odd bodies; treat complete partial.
            progress?.Report(new(
                SefariaOfflineLibraryStage.Downloading,
                $"Using completed Sefaria download ({FormatBytes(existingLength)})",
                existingLength,
                existingLength));
            File.Move(partialPath, destinationPath, true);
            TryDeleteFile(statePath);
            return destinationPath;
        }
        else if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // Full-body response while we had a partial: only restart when the body is clearly the full object.
            var fullLength = contentRange?.Length ?? responseLength;
            if (fullLength is null || fullLength == existingLength)
            {
                // Ambiguous — keep partial and fail rather than silently wiping multi-GiB progress.
                throw new InvalidDataException(
                    "The server did not accept a ranged resume for the partial Sefaria download. " +
                    "Choose Resume again later, or Discard the partial download and start over.");
            }

            if (expectedTotal is { } exp && fullLength != exp && responseLength == exp - existingLength)
            {
                // Body is the remaining tail even though status was 200.
                resumeFrom = existingLength;
                append = true;
            }
            else
            {
                // True full restart (object replaced or server ignores Range).
                TryDeleteFile(partialPath);
                resumeFrom = 0;
                append = false;
            }
        }
        else
        {
            resumeFrom = 0;
            append = false;
        }

        var totalLength = contentRange?.Length ??
            (responseLength is null
                ? expectedTotal
                : append
                    ? resumeFrom + responseLength.Value
                    : responseLength);

        var newState = new DownloadState
        {
            Source = source.ToString(),
            ETag = response.Headers.ETag?.ToString() ?? state?.ETag,
            LastModifiedUtc = response.Content.Headers.LastModified ?? state?.LastModifiedUtc,
            ExpectedBytes = totalLength ?? expectedTotal
        };
        await WriteStateAsync(statePath, newState, cancellationToken);

        if (resumeFrom > 0)
        {
            progress?.Report(new(
                SefariaOfflineLibraryStage.Downloading,
                $"Resuming Sefaria library download ({FormatBytes(resumeFrom)} of {FormatBytes(totalLength)})",
                resumeFrom,
                totalLength));
        }

        long completed;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
                         partialPath,
                         append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            completed = resumeFrom;
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
                    append && resumeFrom > 0
                        ? $"Resuming Sefaria library download ({FormatBytes(completed)} of {FormatBytes(totalLength)})"
                        : $"Downloading Sefaria library ({FormatBytes(completed)} of {FormatBytes(totalLength)})",
                    completed,
                    totalLength));
            }

            await output.FlushAsync(cancellationToken);
        }

        var finalExpected = newState.ExpectedBytes ?? expectedTotal;
        if (finalExpected is not null && completed != finalExpected)
        {
            throw new InvalidDataException(
                $"The download ended at {completed:N0} bytes; {finalExpected:N0} were expected. " +
                "Resume will continue from the partial file.");
        }

        File.Move(partialPath, destinationPath, true);
        TryDeleteFile(statePath);
        return destinationPath;
    }

    private static bool SourceMatches(DownloadState? state, Uri source) =>
        state is null ||
        string.IsNullOrWhiteSpace(state.Source) ||
        string.Equals(state.Source, source.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleteLength(
        long length,
        long? expectedTotal,
        DownloadState? state,
        Uri source)
    {
        if (length <= 0)
        {
            return false;
        }

        if (expectedTotal is { } total && total > 0)
        {
            return length == total;
        }

        if (state?.ExpectedBytes is { } fromState && fromState > 0 && SourceMatches(state, source))
        {
            return length == fromState;
        }

        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
