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
    public async Task<List<SefariaVersionOption>> GetAvailableVersionsAsync(string title, CancellationToken cancellationToken)
    {
        if (TryGetCachedAvailableVersions(title, out var cachedVersions))
        {
            return cachedVersions;
        }

        return new List<SefariaVersionOption>();
    }

    public async Task DownloadBookAsync(
        SefariaBookNode book,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var content = IsTalmudTitle(book.Title)
            ? await DownloadTalmudTractateAsync(book.Title, book.SelectedVersion, progress, cancellationToken)
            : await DownloadWithRetryAsync(book.Title, book.SelectedVersion, progress, cancellationToken);
        if (!IsTalmudTitle(book.Title) &&
            !IsDownloadedBookJson(content) &&
            book.SelectedVersion is not null)
        {
            progress.Report(0);
            var fallbackContent = await DownloadTextApiPageAsync(book.Title, book.SelectedVersion, cancellationToken);
            if (IsDownloadedBookJson(fallbackContent))
            {
                content = fallbackContent;
            }

            progress.Report(100);
        }

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
                return await DownloadTextWithProgressAsync(title, version, progress, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                await Task.Delay(delayMs, cancellationToken);
                delayMs *= 2;
            }
        }

        throw new InvalidOperationException("Download failed after multiple attempts.");
    }



    private async Task<string> DownloadTalmudPageWithTextApiAsync(
        string title,
        SefariaVersionOption version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var reference = GetVersionProbeRef(title);
        var versionParameter = GetTextApiVersionParameter(version);
        var url = $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0&{versionParameter}={Uri.EscapeDataString(version.VersionTitle)}";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        progress.Report(0);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        progress.Report(100);
        return content;
    }

    private async Task<string> DownloadTalmudTractateAsync(
        string title,
        SefariaVersionOption? version,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<JsonElement>();
        var reference = GetVersionProbeRef(title);
        var totalPages = 0;
        var downloadedCount = 0;
        progress.Report(0);

        while (!string.IsNullOrWhiteSpace(reference))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageJson = version is null
                ? await DownloadTextApiPageAsync(reference, null, cancellationToken)
                : await DownloadTextApiPageAsync(reference, version, cancellationToken);

            using var document = JsonDocument.Parse(pageJson);
            var pageRoot = document.RootElement.Clone();
            pages.Add(pageRoot);
            downloadedCount++;

            // Try to get total page count from the first page
            if (totalPages == 0 &&
                pageRoot.TryGetProperty("length", out var lengthElement) &&
                lengthElement.TryGetInt32(out var length))
            {
                totalPages = length;
            }

            // Report progress
            if (totalPages > 0)
            {
                progress.Report(Math.Min(99, ((double)downloadedCount / totalPages) * 100));
            }
            else
            {
                // If we don't know total, estimate conservatively
                progress.Report(Math.Min(99, downloadedCount * 8));
            }

            reference = pageRoot.TryGetProperty("next", out var nextElement)
                ? nextElement.GetString() ?? string.Empty
                : string.Empty;
        }

        progress.Report(100);
        return CreateTalmudTractateJson(title, version, pages);
    }

    private static async Task<string> DownloadTextApiPageAsync(
        string reference,
        SefariaVersionOption? version,
        CancellationToken cancellationToken)
    {
        var url = version is null
            ? $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0"
            : $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(reference)}?context=0&commentary=0&{GetTextApiVersionParameter(version)}={Uri.EscapeDataString(version.VersionTitle)}";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string GetTextApiVersionParameter(SefariaVersionOption version)
    {
        return string.Equals(version.LanguageCode, "he", StringComparison.OrdinalIgnoreCase) ? "vhe" : "ven";
    }

    private static string CreateTalmudTractateJson(
        string title,
        SefariaVersionOption? version,
        List<JsonElement> pages)
    {
        var firstPage = pages.FirstOrDefault();
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["indexTitle"] = title,
            ["pages"] = pages
        };

        if (version is not null)
        {
            payload["language"] = version.LanguageCode;
            payload["versionTitle"] = version.VersionTitle;
        }

        if (firstPage.ValueKind == JsonValueKind.Object)
        {
            CopyJsonString(firstPage, payload, "heIndexTitle");
            CopyJsonString(firstPage, payload, "heBook");
            CopyJsonString(firstPage, payload, "heVersionTitle");
            CopyJsonString(firstPage, payload, "versionTitle");
            if (firstPage.TryGetProperty("categories", out var categories) &&
                categories.ValueKind == JsonValueKind.Array)
            {
                payload["categories"] = categories;
            }
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void CopyJsonString(JsonElement source, Dictionary<string, object?> target, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            target[propertyName] = value.GetString();
        }
    }

    private static async Task<string> DownloadTextWithProgressAsync(
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
        if (version is null ||
            string.IsNullOrWhiteSpace(version.VersionTitle) ||
            string.IsNullOrWhiteSpace(version.LanguageCode))
        {
            return $"https://www.sefaria.org/api/texts/{Uri.EscapeDataString(title)}.json";
        }

        var segment = $"{title} - {version.LanguageCode} - {version.VersionTitle}.json";
        return $"https://www.sefaria.org/download/version/{Uri.EscapeDataString(segment)}";
    }


    private static string NormalizeLanguageCode(string? languageCode, string? languageFamilyName)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            return languageCode.Trim().ToLowerInvariant();
        }

        var family = languageFamilyName?.Trim().ToLowerInvariant();
        return family switch
        {
            "english" => "en",
            "hebrew" => "he",
            _ => string.IsNullOrWhiteSpace(family) ? "en" : family.Length >= 2 ? family[..2] : family
        };
    }

    private string GetVersionProbeRef(string title)
    {
        return IsTalmudTitle(title) ? $"{title} 2a" : $"{title} 1";
    }

    private bool IsTalmudTitle(string title)
    {
        var orderLookup = BuildIndexOrderLookup();
        return orderLookup.TryGetValue(title, out var order) &&
            order.Categories.Any(category => string.Equals(category, "Talmud", StringComparison.OrdinalIgnoreCase));
    }
}
