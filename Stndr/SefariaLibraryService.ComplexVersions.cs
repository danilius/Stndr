using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    public List<ComplexVersionSection> GetComplexVersionSections(string title, string languageCode)
    {
        var schema = GetBookSchema(title);
        if (schema?.RootNode is not { Children.Count: >= 2 } root)
        {
            return new();
        }

        var versions = GetInstalledVersionsForTitle(title)
            .Where(version => string.Equals(
                version.LanguageCode,
                languageCode,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        EnsureNodeCoverage(versions, root);

        var sections = new List<ComplexVersionSection>(root.Children.Count);
        foreach (var node in root.Children)
        {
            if (string.IsNullOrWhiteSpace(node.Key))
            {
                continue;
            }

            var available = versions
                .Where(version => version.NodeCoverage.TryGetValue(node.Key, out var coverage) &&
                                  coverage.Segments > 0)
                .ToList();
            var maximumSegments = available.Count == 0
                ? 0
                : available.Max(version => version.NodeCoverage[node.Key].Segments);
            var candidates = available
                .OrderByDescending(version =>
                    maximumSegments > 0 &&
                    version.NodeCoverage[node.Key].Segments * 4 >= maximumSegments)
                .ThenByDescending(version => version.OfflineIsPrimary)
                .ThenByDescending(version => version.OfflineIsSource)
                .ThenByDescending(version => version.OfflinePriority)
                .ThenByDescending(version => version.NodeCoverage[node.Key].Segments)
                .ThenByDescending(version => version.NodeCoverage[node.Key].Characters)
                .ThenBy(version => version.VersionTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(version => version.UpstreamVersionId, StringComparer.Ordinal)
                .ToList();

            sections.Add(new ComplexVersionSection(
                node.Key,
                FirstComplexNodeTitle(node),
                node.HeTitle,
                candidates));
        }

        return sections;
    }

    public InstalledSefariaBook? CreateBestAvailableComplexVersion(
        string title,
        string languageCode,
        IReadOnlyDictionary<string, string>? preferredVersions = null)
    {
        var sections = GetComplexVersionSections(title, languageCode);
        if (sections.Count < 2)
        {
            return null;
        }

        var selected = new Dictionary<string, InstalledSefariaBook>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var preferredKey = preferredVersions is not null &&
                               preferredVersions.TryGetValue(section.Key, out var savedKey)
                ? savedKey
                : string.Empty;
            var source = section.Versions.FirstOrDefault(version =>
                             string.Equals(version.StableVersionKey, preferredKey, StringComparison.Ordinal)) ??
                         section.Versions.FirstOrDefault();
            if (source is not null)
            {
                selected[section.Key] = source;
            }
        }

        if (selected.Count < 2)
        {
            return null;
        }

        var representative = selected.Values.First();
        var composite = CloneInstalledBook(representative);
        composite.UpstreamVersionId = string.Empty;
        composite.VersionTitle = "Best available";
        composite.FilePath = $"sefaria-library://composite/{Uri.EscapeDataString(title)}/{languageCode}";
        composite.License = "Multiple sources";
        composite.VersionSource = string.Empty;
        composite.CompositeSectionVersionIds = selected.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OfflineVersionId,
            StringComparer.Ordinal);
        composite.NodeCoverage = selected.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.NodeCoverage[pair.Key],
            StringComparer.Ordinal);
        composite.SegmentCount = composite.NodeCoverage.Values.Sum(value => value.Segments);
        composite.CharacterCount = composite.NodeCoverage.Values.Sum(value => value.Characters);
        return composite;
    }

    public static string ComplexSectionPreferenceKey(string workTitle, string languageCode, string nodeKey) =>
        $"{workTitle}|{languageCode}|{nodeKey}";

    private void EnsureNodeCoverage(
        IReadOnlyList<InstalledSefariaBook> versions,
        SefariaSchemaNode schemaRoot)
    {
        if (versions.Count == 0 || versions.Any(version => version.NodeCoverage.Count > 0))
        {
            return;
        }

        foreach (var version in versions.Where(version => version.IsOfflineLibraryVersion))
        {
            using var document = JsonDocument.Parse(ReadOfflineVersionJson(version.OfflineVersionId));
            if (!document.RootElement.TryGetProperty("text", out var text) ||
                text.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var node in schemaRoot.Children)
            {
                if (string.IsNullOrWhiteSpace(node.Key) ||
                    !text.TryGetProperty(node.Key, out var nodeText))
                {
                    continue;
                }

                var coverage = CountJsonText(nodeText);
                if (coverage.Segments > 0)
                {
                    version.NodeCoverage[node.Key] = coverage;
                }
            }
        }
    }

    private static VersionNodeCoverage CountJsonText(JsonElement element)
    {
        long segments = 0;
        long characters = 0;

        void Walk(JsonElement current)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.String:
                    var value = current.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        segments++;
                        characters += value.Length;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var child in current.EnumerateArray())
                    {
                        Walk(child);
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var child in current.EnumerateObject())
                    {
                        Walk(child.Value);
                    }
                    break;
            }
        }

        Walk(element);
        return new VersionNodeCoverage(segments, characters);
    }

    private string ReadOfflineCompositeVersionJson(InstalledSefariaBook book)
    {
        var compositeText = new JsonObject();
        foreach (var (nodeKey, versionId) in book.CompositeSectionVersionIds)
        {
            var sourceRoot = JsonNode.Parse(ReadOfflineVersionJson(versionId));
            var sourceNode = sourceRoot?["text"]?[nodeKey];
            compositeText[nodeKey] = sourceNode?.DeepClone() ?? new JsonObject();
        }

        return new JsonObject { ["text"] = compositeText }.ToJsonString();
    }

    private static string FirstComplexNodeTitle(SefariaSchemaNode node) =>
        !string.IsNullOrWhiteSpace(node.Title)
            ? node.Title
            : !string.IsNullOrWhiteSpace(node.SharedTitle)
                ? node.SharedTitle
                : node.Key;
}
