using System;
using System.Collections.Generic;
using System.Linq;

namespace Stndr;

internal sealed class SiddurNavigationNode
{
    public ReaderNavigationPart Part { get; }
    public string TargetKey { get; }
    public List<SiddurNavigationNode> Children { get; } = new();

    private SiddurNavigationNode(ReaderNavigationPart part, string targetKey)
    {
        Part = part;
        TargetKey = targetKey;
    }

    internal static List<SiddurNavigationNode> Build(
        IEnumerable<(string TargetKey, IReadOnlyList<ReaderNavigationPart> Path)> entries)
    {
        var roots = new List<SiddurNavigationNode>();
        foreach (var (targetKey, path) in entries)
        {
            var siblings = roots;
            foreach (var part in path)
            {
                var node = siblings.FirstOrDefault(candidate => candidate.Part.Key == part.Key);
                if (node is null)
                {
                    node = new(part, targetKey);
                    siblings.Add(node);
                }
                siblings = node.Children;
            }
        }
        return roots;
    }

    internal IEnumerable<SiddurNavigationNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children.SelectMany(child => child.DescendantsAndSelf())) yield return child;
    }

    internal bool Matches(string query)
    {
        var terms = SiddurPrayerMarkers.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = SiddurPrayerMarkers.Normalize(Part.Title + " " + Part.HebrewTitle);
        return terms.Length > 0 && terms.All(term => text.Contains(term, StringComparison.Ordinal));
    }
}
