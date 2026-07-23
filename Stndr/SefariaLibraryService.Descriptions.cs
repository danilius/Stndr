using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Stndr;

public sealed partial class SefariaLibraryService
{
    private readonly object _workShortDescriptionsGate = new();
    private Dictionary<string, WorkShortDescription>? _workShortDescriptionsCache;

    private sealed record WorkShortDescription(string English, string Hebrew);

    public bool TryGetWorkShortDescription(
        string? title,
        out string? english,
        out string? hebrew)
    {
        english = null;
        hebrew = null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        EnsureWorkShortDescriptionsCache();
        if (_workShortDescriptionsCache is null)
        {
            return false;
        }

        if (TryGetCachedWorkShortDescription(title, out english, out hebrew))
        {
            return true;
        }

        var partialMatch = _workShortDescriptionsCache.Keys
            .Where(key =>
                key.StartsWith(title, StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(key => key.Length)
            .FirstOrDefault();
        if (partialMatch is not null &&
            TryGetCachedWorkShortDescription(partialMatch, out english, out hebrew))
        {
            return true;
        }

        return false;
    }

    private bool TryGetCachedWorkShortDescription(
        string title,
        out string? english,
        out string? hebrew)
    {
        english = null;
        hebrew = null;
        if (_workShortDescriptionsCache is null ||
            !_workShortDescriptionsCache.TryGetValue(title, out var description))
        {
            return false;
        }

        english = string.IsNullOrWhiteSpace(description.English) ? null : description.English;
        hebrew = string.IsNullOrWhiteSpace(description.Hebrew) ? null : description.Hebrew;
        return !string.IsNullOrWhiteSpace(english) || !string.IsNullOrWhiteSpace(hebrew);
    }

    private void WarmWorkShortDescriptionsCache(string indexText)
    {
        lock (_workShortDescriptionsGate)
        {
            _workShortDescriptionsCache = BuildWorkShortDescriptionsCache(indexText);
        }
    }

    private void EnsureWorkShortDescriptionsCache()
    {
        if (_workShortDescriptionsCache is not null)
        {
            return;
        }

        lock (_workShortDescriptionsGate)
        {
            if (_workShortDescriptionsCache is not null)
            {
                return;
            }

            if (HasOfflineLibrary)
            {
                _workShortDescriptionsCache = LoadOfflineWorkDescriptions();
                return;
            }

            if (!IsConfigured || !File.Exists(IndexFilePath))
            {
                _workShortDescriptionsCache = new Dictionary<string, WorkShortDescription>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            try
            {
                var indexText = File.ReadAllText(IndexFilePath);
                _workShortDescriptionsCache = BuildWorkShortDescriptionsCache(indexText);
            }
            catch
            {
                _workShortDescriptionsCache = new Dictionary<string, WorkShortDescription>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static Dictionary<string, WorkShortDescription> BuildWorkShortDescriptionsCache(string indexText)
    {
        var cache = new Dictionary<string, WorkShortDescription>(StringComparer.OrdinalIgnoreCase);
        var nodes = JsonSerializer.Deserialize<List<SefariaIndexJsonNode>>(indexText) ?? new List<SefariaIndexJsonNode>();
        foreach (var node in FlattenIndexNodes(nodes))
        {
            if (string.IsNullOrWhiteSpace(node.Title))
            {
                continue;
            }

            var english = FirstNonEmptyDescription(node.EnShortDesc, node.EnDesc);
            var hebrew = FirstNonEmptyDescription(node.HeShortDesc, node.HeDesc);
            if (string.IsNullOrWhiteSpace(english) && string.IsNullOrWhiteSpace(hebrew))
            {
                continue;
            }

            cache[node.Title] = new WorkShortDescription(english, hebrew);
        }

        return cache;
    }

    private static IEnumerable<SefariaIndexJsonNode> FlattenIndexNodes(IEnumerable<SefariaIndexJsonNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Contents is { Count: > 0 })
            {
                foreach (var child in FlattenIndexNodes(node.Contents))
                {
                    yield return child;
                }

                continue;
            }

            yield return node;
        }
    }

    private static string FirstNonEmptyDescription(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
