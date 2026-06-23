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
        var textDownload = DownloadWithRetryAsync(book.Title, book.SelectedVersion, progress, cancellationToken);
        var schemaDownload = EnsureSchemaDownloadedAsync(book.Title, cancellationToken);

        var content = await textDownload;
        ValidateDownloadedBookJson(content, book.Title, book.SelectedVersion);
        var filePath = GetBookFilePath(book.Title, book.SelectedVersion);
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);
        UpsertInstalledBook(CreateInstalledBookRecord(book, filePath, content));

        // Schema download runs in background; UI never waits for it
        _ = schemaDownload;
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

    public async Task EnsureSchemaDownloadedAsync(string title, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return;

        var schemaPath = GetSchemaFilePath(title);
        if (File.Exists(schemaPath))
            return;

        var key = GetSchemaKey(title);
        // Schema filenames in the export use underscores for spaces (e.g. "Rosh_Hashanah" not "Rosh Hashanah")
        var url = $"https://storage.googleapis.com/sefaria-export/schemas/{Uri.EscapeDataString(key)}.json";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogSchemaIssue(title, $"Schema not available: HTTP {response.StatusCode}");
                return;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
            await File.WriteAllTextAsync(schemaPath, content, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            LogSchemaIssue(title, ex.Message);
        }
    }

    private void LogSchemaIssue(string title, string message)
    {
        try
        {
            var logDir = Path.Combine(StorageRootFolder, "logs");
            Directory.CreateDirectory(logDir);
            var key = GetSchemaKey(title);
            var logPath = Path.Combine(logDir, $"schema-{GetSafeFileName(key)}.log");
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] {message}{Environment.NewLine}");
        }
        catch
        {
            // swallow logging errors
        }
    }
}
