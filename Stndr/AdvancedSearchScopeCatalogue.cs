using System;
using System.Collections.Generic;
using System.Linq;

namespace Stndr;

/// <summary>
/// Lightweight scope tree used by Advanced Search. Built off the UI thread and cached so the
/// scope dialog never materializes thousands of Avalonia controls up front.
/// </summary>
internal sealed class AdvancedSearchScopeCatalogueNode
{
    public required AdvancedSearchScopeCatalogueKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string HebrewLabel { get; init; } = string.Empty;
    public List<AdvancedSearchScopeCatalogueNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;
}

internal enum AdvancedSearchScopeCatalogueKind
{
    Category,
    Work
}

internal static class AdvancedSearchScopeCatalogue
{
    public static IReadOnlyList<AdvancedSearchScopeCatalogueNode> FromSefariaRoot(SefariaCategoryNode root)
    {
        return root.Contents
            .OrderBy(node => node.Order)
            .Select(ConvertSefariaNode)
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList();
    }

    public static IReadOnlyList<AdvancedSearchScopeCatalogueNode> FromInstalledRoots(IEnumerable<object> roots)
    {
        return roots
            .Select(ConvertInstalledNode)
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList();
    }

    /// <summary>
    /// Returns a pruned copy of the tree containing only nodes that match the filter or have a
    /// matching descendant. Empty filter returns the original roots.
    /// </summary>
    public static IReadOnlyList<AdvancedSearchScopeCatalogueNode> Filter(
        IReadOnlyList<AdvancedSearchScopeCatalogueNode> roots,
        string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return roots;
        }

        var trimmed = filter.Trim();
        var filtered = new List<AdvancedSearchScopeCatalogueNode>();
        foreach (var root in roots)
        {
            var match = FilterNode(root, trimmed);
            if (match is not null)
            {
                filtered.Add(match);
            }
        }

        return filtered;
    }

    private static AdvancedSearchScopeCatalogueNode? FilterNode(
        AdvancedSearchScopeCatalogueNode node,
        string filter)
    {
        var childMatches = new List<AdvancedSearchScopeCatalogueNode>();
        foreach (var child in node.Children)
        {
            var match = FilterNode(child, filter);
            if (match is not null)
            {
                childMatches.Add(match);
            }
        }

        var selfMatches = NodeMatches(node, filter);
        if (!selfMatches && childMatches.Count == 0)
        {
            return null;
        }

        var copy = new AdvancedSearchScopeCatalogueNode
        {
            Kind = node.Kind,
            Key = node.Key,
            Label = node.Label,
            HebrewLabel = node.HebrewLabel
        };
        copy.Children.AddRange(childMatches);
        return copy;
    }

    private static bool NodeMatches(AdvancedSearchScopeCatalogueNode node, string filter) =>
        node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        node.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(node.HebrewLabel) &&
         node.HebrewLabel.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static AdvancedSearchScopeCatalogueNode? ConvertSefariaNode(SefariaNode node)
    {
        switch (node)
        {
            case SefariaCategoryNode category:
            {
                var result = new AdvancedSearchScopeCatalogueNode
                {
                    Kind = AdvancedSearchScopeCatalogueKind.Category,
                    Key = category.DisplayTitle,
                    Label = category.DisplayTitle,
                    HebrewLabel = category.HebrewCategory ?? string.Empty
                };
                foreach (var child in category.Contents.OrderBy(item => item.Order))
                {
                    var converted = ConvertSefariaNode(child);
                    if (converted is not null)
                    {
                        result.Children.Add(converted);
                    }
                }

                return result;
            }
            case SefariaBookNode book:
                return new AdvancedSearchScopeCatalogueNode
                {
                    Kind = AdvancedSearchScopeCatalogueKind.Work,
                    Key = book.Title,
                    Label = book.Title,
                    HebrewLabel = book.HebrewTitle ?? string.Empty
                };
            default:
                return null;
        }
    }

    private static AdvancedSearchScopeCatalogueNode? ConvertInstalledNode(object node)
    {
        switch (node)
        {
            case InstalledSefariaCategory { IsBookTitle: false } category:
            {
                var result = new AdvancedSearchScopeCatalogueNode
                {
                    Kind = AdvancedSearchScopeCatalogueKind.Category,
                    Key = string.IsNullOrWhiteSpace(category.CategoryPath) ? category.Title : category.CategoryPath,
                    Label = category.Title,
                    HebrewLabel = category.HebrewTitle ?? string.Empty
                };
                foreach (var child in category.Children)
                {
                    var converted = ConvertInstalledNode(child);
                    if (converted is not null)
                    {
                        result.Children.Add(converted);
                    }
                }

                return result;
            }
            case InstalledSefariaCategory { IsBookTitle: true } bookCategory:
                return new AdvancedSearchScopeCatalogueNode
                {
                    Kind = AdvancedSearchScopeCatalogueKind.Work,
                    Key = bookCategory.Title,
                    Label = bookCategory.Title,
                    HebrewLabel = bookCategory.HebrewTitle ?? string.Empty
                };
            case InstalledSefariaBook book:
                return new AdvancedSearchScopeCatalogueNode
                {
                    Kind = AdvancedSearchScopeCatalogueKind.Work,
                    Key = book.Title,
                    Label = book.Title,
                    HebrewLabel = book.HebrewTitle ?? string.Empty
                };
            default:
                return null;
        }
    }
}
