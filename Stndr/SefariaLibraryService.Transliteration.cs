using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private const string NameApiBaseUrl = "https://www.sefaria.org/api/name/";

    /// <summary>
    /// Queries the Sefaria name API with <paramref name="query"/> and returns the canonical
    /// book/text keys that the query resolves to (e.g. "bereshit" → ["Genesis", "Bereshit Rabbah"]).
    /// These keys match the <c>Title</c> property on <see cref="SefariaBookNode"/> so callers can
    /// surface results for transliterated input such as "bereshit", "shemot", or "makkot".
    ///
    /// The method checks two sources in the API response:
    /// <list type="number">
    ///   <item>The top-level <c>index</c> field — the primary book the query resolves to.</item>
    ///   <item>All <c>completion_objects</c> entries whose <c>type</c> is <c>"ref"</c>.</item>
    /// </list>
    ///
    /// Returns an empty list on any network or parse failure so that callers degrade gracefully
    /// and the local title/Hebrew search still works without transliteration.
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchTransliterationMatchKeysAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        try
        {
            var url = NameApiBaseUrl + Uri.EscapeDataString(query.Trim());
            using var response = await HttpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = document.RootElement;
            var keys = new List<string>();

            // Primary resolution: the canonical index key the query maps to (e.g. "Genesis")
            if (root.TryGetProperty("is_ref", out var isRefProp) &&
                isRefProp.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("index", out var indexProp) &&
                indexProp.GetString() is { Length: > 0 } primaryKey)
            {
                keys.Add(primaryKey);
            }

            // Additional completions: other ref-type texts whose name starts with the query
            if (root.TryGetProperty("completion_objects", out var completions) &&
                completions.ValueKind == JsonValueKind.Array)
            {
                foreach (var completion in completions.EnumerateArray())
                {
                    if (!completion.TryGetProperty("type", out var typeProp) ||
                        typeProp.GetString() != "ref")
                    {
                        continue;
                    }

                    if (completion.TryGetProperty("key", out var keyProp) &&
                        keyProp.GetString() is { Length: > 0 } key &&
                        !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys;
        }
        catch (OperationCanceledException)
        {
            throw; // Let callers handle cancellation normally
        }
        catch
        {
            // Network error, timeout, or unexpected JSON shape — fail silently so the
            // local title/Hebrew search still works without transliteration support.
            return Array.Empty<string>();
        }
    }
}
