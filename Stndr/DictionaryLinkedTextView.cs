using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Stndr;

internal sealed record DictionaryCitationLink(
    int Start,
    int Length,
    string DisplayText,
    string FullReference,
    string WorkTitle);

internal sealed class DictionaryLinkedTextView : SelectableTextBlock
{
    private const double ClickMovementTolerance = 4;
    private static readonly IBrush CitationBrush = new SolidColorBrush(Color.Parse("#005EA8"));

    private readonly string _text;
    private readonly IReadOnlyList<DictionaryCitationLink> _citations;
    private readonly Action<DictionaryCitationLink> _openCitation;
    private Point? _pressPoint;

    public DictionaryLinkedTextView(
        string text,
        IEnumerable<DictionaryCitationLink> citations,
        FlowDirection flowDirection,
        Action<DictionaryCitationLink> openCitation)
    {
        _text = text;
        _citations = citations.ToList();
        _openCitation = openCitation;
        FlowDirection = flowDirection;
        TextAlignment = flowDirection == FlowDirection.RightToLeft ? TextAlignment.Right : TextAlignment.Left;
        TextWrapping = TextWrapping.Wrap;

        var inlines = Inlines ?? new InlineCollection();
        inlines.Clear();
        var position = 0;
        foreach (var citation in _citations)
        {
            if (citation.Start > position)
            {
                inlines.Add(new Run { Text = text[position..citation.Start] });
            }

            inlines.Add(new Run
            {
                Text = citation.DisplayText,
                Foreground = CitationBrush,
                TextDecorations = Avalonia.Media.TextDecorations.Underline
            });
            position = citation.Start + citation.Length;
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
        base.OnPointerPressed(e);
        _pressPoint = e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            ? e.GetPosition(this)
            : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is not { } pressPoint)
        {
            return;
        }

        var releasePoint = e.GetPosition(this);
        _pressPoint = null;
        if (Math.Abs(releasePoint.X - pressPoint.X) > ClickMovementTolerance ||
            Math.Abs(releasePoint.Y - pressPoint.Y) > ClickMovementTolerance ||
            TextLayout is null)
        {
            return;
        }

        var hit = TextLayout.HitTestPoint(new Point(
            releasePoint.X - Padding.Left,
            releasePoint.Y - Padding.Top));
        var position = Math.Clamp(hit.TextPosition, 0, Math.Max(0, _text.Length - 1));
        var citation = _citations.FirstOrDefault(candidate =>
            position >= candidate.Start && position < candidate.Start + candidate.Length);
        if (citation is null)
        {
            return;
        }

        _openCitation(citation);
        e.Handled = true;
    }
}
