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
    public async Task DownloadBookAsync(
        SefariaBookNode book,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var content = await DownloadWithRetryAsync(book.Title, book.SelectedVersion, progress, cancellationToken);
        ValidateDownloadedBookJson(content, book.Title, book.SelectedVersion);
        var filePath = GetBookFilePath(book.Title, book.SelectedVersion);
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);
        UpsertInstalledBook(CreateInstalledBookRecord(book, filePath, content));
    }

    private async Task<string> DownloadWithRetryAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var delayMs = 1000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await DownloadExportTextWithProgressAsync(title, version, progress, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
            }
        }

        throw new InvalidOperationException("Download failed after multiple attempts.");
    }

    private static async Task<string> DownloadExportTextWithProgressAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var url = BuildDownloadUrl(title, version);
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalLength = response.Content.Headers.ContentLength ?? -1L;
        if (totalLength <= 0)
        {
            progress.Report(0);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            progress.Report(100);
            return content;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        long totalRead = 0;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
            builder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            progress.Report((double)totalRead / totalLength * 100);
        }

        progress.Report(100);
        return builder.ToString();
    }

    private static string BuildDownloadUrl(string title, SefariaVersionOption? version)
    {
        if (version is null || string.IsNullOrWhiteSpace(version.DownloadUrl))
        {
            throw new InvalidOperationException($"No export download URL is available for {title}.");
        }

        return EncodeExportUrl(version.DownloadUrl);
    }

    private static string EncodeExportUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
        {
            return url;
        }

        var queryIndex = url.IndexOfAny(new[] { '?', '#' });
        var baseUrl = queryIndex >= 0 ? url[..queryIndex] : url;
        var suffix = queryIndex >= 0 ? url[queryIndex..] : string.Empty;
        var hostPrefix = $"{absoluteUri.Scheme}://{absoluteUri.Authority}";
        var rawPath = baseUrl.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase)
            ? baseUrl[hostPrefix.Length..]
            : absoluteUri.AbsolutePath;

        var encodedPath = string.Join("/",
            rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => Uri.EscapeDataString(Uri.UnescapeDataString(segment))));

        return $"{hostPrefix}/{encodedPath}{suffix}";
    }
}
