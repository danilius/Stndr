using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Stndr;

internal enum DictionaryTextLinkKind
{
    Citation,
    Headword
}

internal sealed record DictionaryTextLink(
    int Start,
    int Length,
    string DisplayText,
    string Target,
    string Context,
    DictionaryTextLinkKind Kind);

internal sealed record DictionaryHeadwordCrossReference(
    int Start,
    int Length,
    string DisplayText,
    string Headword);

internal static class DictionaryHeadwordCrossReferenceParser
{
    private static readonly Regex CrossReferenceRegex = new(
        @"(?<![\p{L}\p{N}])(?<display>v\.\s+(?:also\s+)?(?<headword>[\u0590-\u05FF][\u0590-\u05FF'""\u05F3\u05F4\u05BE-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'""\u05F3\u05F4\u05BE-]*){0,3})(?:\s+(?<sense>IV|V|I{1,3}))?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<DictionaryHeadwordCrossReference> Find(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<DictionaryHeadwordCrossReference>();
        }

        return CrossReferenceRegex.Matches(text)
            .Cast<Match>()
            .Where(match => match.Success)
            .Select(match =>
            {
                var headword = match.Groups["headword"].Value.Trim();
                var sense = match.Groups["sense"].Value.Trim();
                var target = sense.Length == 0 ? headword : $"{headword} {sense}";
                return new DictionaryHeadwordCrossReference(
                    match.Index,
                    match.Length,
                    match.Groups["display"].Value,
                    target);
            })
            .ToList();
    }
}

internal sealed class DictionaryLinkedTextView : SelectableTextBlock
{
    private const double ClickMovementTolerance = 4;
    private static readonly IBrush CitationBrush = new SolidColorBrush(Color.Parse("#005EA8"));
    private static readonly IBrush SelectionHighlightBrush = new SolidColorBrush(Color.Parse("#665AA9E6"));

    private readonly string _text;
    private readonly IReadOnlyList<DictionaryTextLink> _links;
    private readonly Action<DictionaryTextLink> _openLink;
    private Point? _pressPoint;
    private int _selectionAnchor = -1;
    private bool _isSelecting;

    public DictionaryLinkedTextView(
        string text,
        IEnumerable<DictionaryTextLink> links,
        FlowDirection flowDirection,
        Action<DictionaryTextLink> openLink)
    {
        _text = text;
        _links = links.OrderBy(link => link.Start).ToList();
        _openLink = openLink;
        FlowDirection = flowDirection;
        Focusable = true;
        TextAlignment = flowDirection == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
        TextWrapping = TextWrapping.Wrap;
        SelectionBrush = SelectionHighlightBrush;
        SelectionForegroundBrush = Brushes.Black;

        var inlines = Inlines ?? new InlineCollection();
        inlines.Clear();
        var position = 0;
        foreach (var link in _links)
        {
            if (link.Start > position)
            {
                inlines.Add(new Run { Text = text[position..link.Start] });
            }

            inlines.Add(new Run
            {
                Text = link.DisplayText,
                Foreground = CitationBrush,
                TextDecorations = Avalonia.Media.TextDecorations.Underline
            });
            position = link.Start + link.Length;
        }

        if (position < text.Length)
        {
            inlines.Add(new Run { Text = text[position..] });
        }

        Text = null;
        Inlines = inlines;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (updateKind != PointerUpdateKind.LeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }
        if (e.ClickCount > 1)
        {
            _pressPoint = null;
            _selectionAnchor = -1;
            _isSelecting = false;
            base.OnPointerPressed(e);
            return;
        }

        var point = e.GetPosition(this);
        var position = GetTextPosition(point);
        if (position is null)
        {
            base.OnPointerPressed(e);
            return;
        }

        Focus();
        _pressPoint = point;
        _selectionAnchor = position.Value;
        _isSelecting = true;
        SelectionStart = position.Value;
        SelectionEnd = position.Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_isSelecting)
        {
            base.OnPointerMoved(e);
            return;
        }

        UpdateSelection(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_isSelecting || _pressPoint is not { } pressPoint)
        {
            base.OnPointerReleased(e);
            return;
        }

        var releasePoint = e.GetPosition(this);
        UpdateSelection(releasePoint);
        _isSelecting = false;
        _selectionAnchor = -1;
        _pressPoint = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (Math.Abs(releasePoint.X - pressPoint.X) > ClickMovementTolerance ||
            Math.Abs(releasePoint.Y - pressPoint.Y) > ClickMovementTolerance)
        {
            return;
        }

        var position = GetTextPosition(releasePoint);
        if (position is null)
        {
            return;
        }
        var link = _links.FirstOrDefault(candidate =>
            position.Value >= candidate.Start && position.Value < candidate.Start + candidate.Length);
        if (link is null)
        {
            return;
        }

        _openLink(link);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isSelecting = false;
        _selectionAnchor = -1;
        _pressPoint = null;
    }

    private void UpdateSelection(Point point)
    {
        if (_selectionAnchor < 0 || GetTextPosition(point) is not { } position)
        {
            return;
        }

        SelectionStart = Math.Min(_selectionAnchor, position);
        SelectionEnd = Math.Max(_selectionAnchor, position);
    }

    private int? GetTextPosition(Point point)
    {
        if (TextLayout is null || TextLayout.TextLines.Count == 0)
        {
            return null;
        }

        var hit = TextLayout.HitTestPoint(new Point(
            point.X - Padding.Left,
            point.Y - Padding.Top));
        var position = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        return Math.Clamp(position, 0, _text.Length);
    }
}
