using System;
using System.Collections.Generic;
using System.Linq;

namespace Stndr;

/// <summary>
/// Builds Sefaria-style full references from internal unit paths and work titles.
/// Shared by the reader (commentary/link anchors) so formats stay consistent.
/// </summary>
public static class SefariaReferenceFormatting
{
    /// <summary>
    /// Builds a full anchor ref such as <c>Genesis 1:1</c>, <c>Berakhot 2a:12</c>,
    /// or <c>Guide for the Perplexed, Part 1 1:1</c>.
    /// </summary>
    public static string BuildFullAnchorRef(string workTitle, string unitReference)
    {
        if (string.IsNullOrWhiteSpace(workTitle))
        {
            return string.Empty;
        }

        var relative = NormalizeUnitPathToRelative(unitReference);
        if (string.IsNullOrWhiteSpace(relative))
        {
            return string.Empty;
        }

        // Named multi-node works need a comma after the title. Numeric / daf addresses use a space.
        return UsesCommaAfterTitle(relative)
            ? $"{workTitle.Trim()}, {relative}"
            : $"{workTitle.Trim()} {relative}";
    }

    /// <summary>
    /// Converts an internal unit path (e.g. <c>Part 1.default.1.1</c> or <c>2a.12</c>) into a
    /// Sefaria-style relative reference (<c>Part 1 1:1</c> or <c>2a:12</c>).
    /// </summary>
    public static string NormalizeUnitPathToRelative(string? unitReference)
    {
        if (string.IsNullOrWhiteSpace(unitReference))
        {
            return string.Empty;
        }

        var parts = unitReference
            .Trim()
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part =>
                !string.Equals(part, "default", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "nodes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(part, "schema", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var named = new List<string>();
        var numeric = new List<string>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out _))
            {
                numeric.Add(part);
            }
            else
            {
                named.Add(part);
            }
        }

        // Talmud daf path: "2a" + "12" → "2a:12" (not "2a 12").
        if (named.Count == 1 && numeric.Count > 0 && StartsWithDigit(named[0]))
        {
            return $"{named[0]}:{string.Join(":", numeric)}";
        }

        if (named.Count > 0 && numeric.Count > 0)
        {
            return $"{string.Join(", ", named)} {string.Join(":", numeric)}";
        }

        if (numeric.Count > 0)
        {
            return string.Join(":", numeric);
        }

        // Single daf page with no segment, or only named nodes.
        if (named.Count == 1 && StartsWithDigit(named[0]))
        {
            return named[0];
        }

        return string.Join(", ", named);
    }

    /// <summary>
    /// True when the relative address begins with a named structural node (Part 1, Introduction…),
    /// not a simple chapter number or Talmud daf (2a, 30b:5).
    /// </summary>
    public static bool UsesCommaAfterTitle(string relativeReference)
    {
        if (string.IsNullOrWhiteSpace(relativeReference))
        {
            return false;
        }

        var first = relativeReference.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        // "1:1", "12", "2a", "2a:12" all start with a digit → space after title.
        // "Part", "Introduction" start with a letter → comma after title.
        return first.Length > 0 && !char.IsDigit(first[0]);
    }

    private static bool StartsWithDigit(string value) =>
        value.Length > 0 && char.IsDigit(value[0]);
}
