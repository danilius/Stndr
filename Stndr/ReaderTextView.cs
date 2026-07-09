using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Stndr;

public sealed class ReaderTextView : SelectableTextBlock
{
    private static readonly IBrush ReaderSelectionBrush = new SolidColorBrush(Color.Parse("#335AA9E6"));
    private const double ParagraphClickMovementTolerance = 4;
    private readonly MenuItem _copyMenuItem;
    private string _plainText = string.Empty;
    private Point? _leftPressPoint;
    private bool _isSelectingHebrewText;
    private int _hebrewSelectionAnchor = -1;

    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<ReaderTextView, string>(nameof(SourceText), string.Empty);

    public static readonly StyledProperty<bool> IsHebrewProperty =
        AvaloniaProperty.Register<ReaderTextView, bool>(nameof(IsHebrew));

    public static readonly StyledProperty<HebrewMarksMode> HebrewMarksModeProperty =
        AvaloniaProperty.Register<ReaderTextView, HebrewMarksMode>(nameof(HebrewMarksMode), HebrewMarksMode.NikkudAndCantillation);

    public static readonly StyledProperty<FontFamily?> EmbeddedHebrewFontFamilyProperty =
        AvaloniaProperty.Register<ReaderTextView, FontFamily?>(nameof(EmbeddedHebrewFontFamily));

    public ReaderTextView()
    {
        TextWrapping = TextWrapping.Wrap;
        TextTrimming = TextTrimming.None;
        Background = Brushes.Transparent;
        SelectionBrush = ReaderSelectionBrush;
        SelectionForegroundBrush = Brushes.Black;
        _copyMenuItem = new MenuItem
        {
            Header = "Copy"
        };
        _copyMenuItem.Click += async (_, _) => await CopyCurrentTextAsync();
        var dictionaryMenuItem = new MenuItem
        {
            Header = "Dictionary"
        };
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(_copyMenuItem);
        contextMenu.Items.Add(dictionaryMenuItem);
        ContextMenu = contextMenu;
        ContextMenu.Opening += OnContextMenuOpening;
        AddHandler(PointerPressedEvent, TrackParagraphClickStart, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, SelectParentParagraphOnClick, RoutingStrategies.Tunnel);
        UpdateTextDirection();
    }

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public bool IsHebrew
    {
        get => GetValue(IsHebrewProperty);
        set => SetValue(IsHebrewProperty, value);
    }

    public HebrewMarksMode HebrewMarksMode
    {
        get => GetValue(HebrewMarksModeProperty);
        set => SetValue(HebrewMarksModeProperty, value);
    }

    public FontFamily? EmbeddedHebrewFontFamily
    {
        get => GetValue(EmbeddedHebrewFontFamilyProperty);
        set => SetValue(EmbeddedHebrewFontFamilyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceTextProperty ||
            change.Property == IsHebrewProperty ||
            change.Property == HebrewMarksModeProperty ||
            change.Property == EmbeddedHebrewFontFamilyProperty ||
            change.Property == FontFamilyProperty ||
            change.Property == FontSizeProperty ||
            change.Property == ForegroundProperty)
        {
            if (change.Property == IsHebrewProperty)
            {
                UpdateTextDirection();
            }

            ApplyReaderText();
        }
    }

    private void UpdateTextDirection()
    {
        FlowDirection = IsHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TextAlignment = IsHebrew ? TextAlignment.Start : TextAlignment.Left;
    }

    private void ApplyReaderText()
    {
        Inlines?.Clear();
        var parsedRuns = CreateParsedRuns(SourceText);
        _plainText = BuildPlainText(parsedRuns);
        if (parsedRuns.Count == 0)
        {
            Text = string.Empty;
            return;
        }

        Text = null;
        var inlines = Inlines ?? new InlineCollection();
        foreach (var parsedRun in parsedRuns)
        {
            AppendInline(inlines, parsedRun);
        }

        Inlines = inlines;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.LeftButtonPressed && HandleMultiClickSelection(e))
        {
            return;
        }

        if (IsHebrew && updateKind == PointerUpdateKind.LeftButtonPressed)
        {
            BeginHebrewTextSelection(e);
            return;
        }

        base.OnPointerPressed(e);

        if (updateKind != PointerUpdateKind.RightButtonPressed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedText) &&
            this.FindAncestorOfType<ListBoxItem>() is { } listBoxItem)
        {
            listBoxItem.IsSelected = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_isSelectingHebrewText)
        {
            UpdateHebrewTextSelection(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isSelectingHebrewText)
        {
            UpdateHebrewTextSelection(e.GetPosition(this));
            _isSelectingHebrewText = false;
            _hebrewSelectionAnchor = -1;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    private void TrackParagraphClickStart(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            _leftPressPoint = null;
            return;
        }

        _leftPressPoint = e.GetPosition(this);
    }

    private void SelectParentParagraphOnClick(object? sender, PointerReleasedEventArgs e)
    {
        if (_leftPressPoint is not { } pressPoint)
        {
            return;
        }

        var releasePoint = e.GetPosition(this);
        _leftPressPoint = null;

        if (Math.Abs(releasePoint.X - pressPoint.X) > ParagraphClickMovementTolerance ||
            Math.Abs(releasePoint.Y - pressPoint.Y) > ParagraphClickMovementTolerance)
        {
            return;
        }

        if (this.FindAncestorOfType<ListBoxItem>() is { } listBoxItem)
        {
            listBoxItem.IsSelected = true;
        }
    }

    private bool HandleMultiClickSelection(PointerPressedEventArgs e)
    {
        if (e.ClickCount >= 3)
        {
            _leftPressPoint = null;
            SelectParagraphText();
            e.Handled = true;
            return true;
        }

        if (e.ClickCount != 2)
        {
            return false;
        }

        var hitInfo = GetTextHitInfo(e.GetPosition(this));
        if (hitInfo is null)
        {
            return false;
        }

        _leftPressPoint = null;
        SelectWordAt(hitInfo.TextPosition);
        e.Handled = true;
        return true;
    }

    private void BeginHebrewTextSelection(PointerPressedEventArgs e)
    {
        var hitInfo = GetTextHitInfo(e.GetPosition(this));
        if (hitInfo is null)
        {
            ClearSelection();
            return;
        }

        _isSelectingHebrewText = true;
        _hebrewSelectionAnchor = hitInfo.TextPosition;
        SelectionStart = hitInfo.TextPosition;
        SelectionEnd = hitInfo.TextPosition;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void UpdateHebrewTextSelection(Point point)
    {
        if (_hebrewSelectionAnchor < 0)
        {
            return;
        }

        var hitInfo = GetTextHitInfo(point);
        if (hitInfo is null)
        {
            return;
        }

        SelectionStart = Math.Min(_hebrewSelectionAnchor, hitInfo.TextPosition);
        SelectionEnd = Math.Max(_hebrewSelectionAnchor, hitInfo.TextPosition);
    }

    private HebrewHitInfo? GetTextHitInfo(Point point)
    {
        var textLayout = TextLayout;
        if (textLayout is null || textLayout.TextLines.Count == 0)
        {
            return null;
        }

        var hitPoint = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = textLayout.HitTestPoint(hitPoint);
        var rawTextPosition = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        var textPosition = Math.Clamp(rawTextPosition, 0, _plainText.Length);

        return new HebrewHitInfo(
            point,
            hitPoint.X,
            hitPoint.Y,
            rawTextPosition,
            hit.IsTrailing,
            hit.IsInside,
            textLayout.Width,
            rawTextPosition,
            textPosition);
    }

    private void SelectParagraphText()
    {
        SelectionStart = 0;
        SelectionEnd = _plainText.Length;
    }

    private void SelectWordAt(int position)
    {
        var (start, end) = GetWordSpan(position);
        SelectionStart = start;
        SelectionEnd = end;
    }

    private (int Start, int End) GetWordSpan(int position)
    {
        if (_plainText.Length == 0)
        {
            return (0, 0);
        }

        var index = Math.Clamp(position, 0, _plainText.Length - 1);
        if (!IsWordCharacter(_plainText[index]) &&
            index > 0 &&
            IsWordCharacter(_plainText[index - 1]))
        {
            index--;
        }

        if (!IsWordCharacter(_plainText[index]))
        {
            return (position, position);
        }

        var start = index;
        while (start > 0 && IsWordCharacter(_plainText[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < _plainText.Length && IsWordCharacter(_plainText[end]))
        {
            end++;
        }

        return (start, end);
    }

    private static bool IsWordCharacter(char character)
    {
        if (char.IsLetterOrDigit(character))
        {
            return true;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(character);
        return category is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark;
    }

    private static bool ContainsHebrewLetter(char character)
    {
        return character >= '\u0590' && character <= '\u05FF';
    }

    private sealed record HebrewHitInfo(
        Point Point,
        double X,
        double Y,
        int HitTextPosition,
        bool IsTrailing,
        bool IsInside,
        double TextLayoutWidth,
        int RawTextPosition,
        int TextPosition)
    {
        public override string ToString()
        {
            return
                $"pos={TextPosition} raw={RawTextPosition} hit={HitTextPosition} " +
                $"trailing={IsTrailing} inside={IsInside} " +
                $"ptr=({Point.X:0.0},{Point.Y:0.0}) xy=({X:0.0},{Y:0.0}) " +
                $"layoutW={TextLayoutWidth:0.0}";
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var copyText = GetCopyText();
        _copyMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(copyText);
    }

    private async System.Threading.Tasks.Task CopyCurrentTextAsync()
    {
        var copyText = GetCopyText();
        if (string.IsNullOrWhiteSpace(copyText))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(copyText);
        }
    }

    private string GetCopyText()
    {
        return string.IsNullOrWhiteSpace(SelectedText) ? _plainText : SelectedText;
    }

    private static string BuildPlainText(IEnumerable<ParsedRun> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            builder.Append(run.Text);
        }

        return builder.ToString();
    }

    private void AppendInline(InlineCollection inlines, ParsedRun parsedRun)
    {
        var remaining = parsedRun.Text.AsSpan();
        while (remaining.Length > 0)
        {
            var lineBreakIndex = remaining.IndexOf('\n');
            var segment = lineBreakIndex < 0 ? remaining : remaining[..lineBreakIndex];
            if (segment.Length > 0)
            {
                AppendSegmentInlines(inlines, segment.ToString(), parsedRun);
            }

            if (lineBreakIndex < 0)
            {
                break;
            }

            inlines.Add(new LineBreak());
            remaining = remaining[(lineBreakIndex + 1)..];
        }
    }

    private void AppendSegmentInlines(InlineCollection inlines, string segment, ParsedRun parsedRun)
    {
        var embeddedHebrewFontFamily = EmbeddedHebrewFontFamily;
        if (IsHebrew || embeddedHebrewFontFamily is null)
        {
            inlines.Add(CreateRun(segment, parsedRun, FontFamily));
            return;
        }

        var start = 0;
        var currentIsHebrew = ContainsHebrewLetter(segment[0]);
        for (var i = 1; i < segment.Length; i++)
        {
            var isHebrew = ContainsHebrewLetter(segment[i]);
            if (isHebrew == currentIsHebrew)
            {
                continue;
            }

            inlines.Add(CreateRun(segment[start..i], parsedRun, currentIsHebrew ? embeddedHebrewFontFamily : FontFamily));
            start = i;
            currentIsHebrew = isHebrew;
        }

        inlines.Add(CreateRun(segment[start..], parsedRun, currentIsHebrew ? embeddedHebrewFontFamily : FontFamily));
    }

    private Run CreateRun(string text, ParsedRun parsedRun, FontFamily fontFamily)
    {
        return new Run(text)
        {
            FontFamily = fontFamily,
            FontSize = FontSize,
            FontStyle = parsedRun.IsItalic ? FontStyle.Italic : FontStyle.Normal,
            FontWeight = parsedRun.IsBold ? FontWeight.Bold : FontWeight.Normal,
            Foreground = Foreground
        };
    }

    private List<ParsedRun> CreateParsedRuns(string? text)
    {
        var runs = new List<ParsedRun>();
        if (string.IsNullOrEmpty(text))
        {
            return runs;
        }

        var suppressedTags = new Stack<string>();
        var boldDepth = 0;
        var italicDepth = 0;
        var position = 0;

        while (position < text.Length)
        {
            if (text[position] == '<')
            {
                var tagEnd = text.IndexOf('>', position + 1);
                if (tagEnd >= 0)
                {
                    var tag = text.Substring(position + 1, tagEnd - position - 1);
                    ApplyTag(tag, ref boldDepth, ref italicDepth, suppressedTags, runs);
                    position = tagEnd + 1;
                    continue;
                }
            }

            var nextTag = text.IndexOf('<', position);
            var length = nextTag < 0 ? text.Length - position : nextTag - position;
            if (suppressedTags.Count == 0)
            {
                AppendTextRun(
                    text.Substring(position, length),
                    boldDepth > 0,
                    italicDepth > 0,
                    runs);
            }

            position += length;
        }

        return NormalizeReaderWhitespace(runs);
    }

    private void AppendTextRun(string source, bool isBold, bool isItalic, List<ParsedRun> runs)
    {
        var decoded = WebUtility.HtmlDecode(source.Replace('\uFFFD', '?'));
        if (IsHebrew)
        {
            decoded = ApplyHebrewMarksMode(decoded);
        }

        if (!string.IsNullOrEmpty(decoded))
        {
            runs.Add(new ParsedRun(decoded, isBold, isItalic));
        }
    }

    private string ApplyHebrewMarksMode(string text)
    {
        if (HebrewMarksMode == HebrewMarksMode.NikkudAndCantillation)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!ShouldSuppressHebrewMark(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private bool ShouldSuppressHebrewMark(char character)
    {
        var code = (int)character;
        var isCantillation = code >= 0x0591 && code <= 0x05AF;
        var isNikkud = (code >= 0x05B0 && code <= 0x05BC) ||
            code == 0x05C1 ||
            code == 0x05C2 ||
            code == 0x05C7;

        return HebrewMarksMode switch
        {
            HebrewMarksMode.TextOnly => isCantillation || isNikkud,
            HebrewMarksMode.Nikkud => isCantillation,
            _ => false
        };
    }

    private static void ApplyTag(
        string tag,
        ref int boldDepth,
        ref int italicDepth,
        Stack<string> suppressedTags,
        List<ParsedRun> runs)
    {
        var normalized = tag.Trim();
        if (normalized.Length == 0 || normalized[0] == '!')
        {
            return;
        }

        var isClosing = normalized.StartsWith('/');
        if (isClosing)
        {
            normalized = normalized[1..].TrimStart();
        }

        var spaceIndex = normalized.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '/' });
        var tagName = (spaceIndex < 0 ? normalized : normalized[..spaceIndex]).ToLowerInvariant();
        var isSuppressedTag = tagName == "sup" || normalized.Contains("footnote", StringComparison.OrdinalIgnoreCase);

        if (suppressedTags.Count > 0)
        {
            if (isClosing && string.Equals(suppressedTags.Peek(), tagName, StringComparison.OrdinalIgnoreCase))
            {
                suppressedTags.Pop();
            }

            return;
        }

        if (isSuppressedTag)
        {
            if (!isClosing)
            {
                suppressedTags.Push(tagName);
            }

            return;
        }

        switch (tagName)
        {
            case "br":
                runs.Add(new ParsedRun("\n", boldDepth > 0, italicDepth > 0));
                break;
            case "p":
                if (isClosing)
                {
                    runs.Add(new ParsedRun("\n\n", boldDepth > 0, italicDepth > 0));
                }
                break;
            case "div":
            case "blockquote":
                if (isClosing)
                {
                    runs.Add(new ParsedRun("\n", boldDepth > 0, italicDepth > 0));
                }
                break;
            case "b":
            case "strong":
                boldDepth = Math.Max(0, boldDepth + (isClosing ? -1 : 1));
                break;
            case "i":
            case "em":
                italicDepth = Math.Max(0, italicDepth + (isClosing ? -1 : 1));
                break;
        }
    }

    private static List<ParsedRun> NormalizeReaderWhitespace(IEnumerable<ParsedRun> runs)
    {
        var normalized = new List<ParsedRun>();
        var previousWasSpace = false;

        foreach (var run in runs)
        {
            var builder = new StringBuilder(run.Text.Length);
            foreach (var character in run.Text)
            {
                if (character == '\r')
                {
                    continue;
                }

                if (character == '\n')
                {
                    while (builder.Length > 0 && builder[^1] == ' ')
                    {
                        builder.Length--;
                    }

                    builder.Append('\n');
                    previousWasSpace = false;
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasSpace)
                    {
                        builder.Append(' ');
                        previousWasSpace = true;
                    }

                    continue;
                }

                builder.Append(character);
                previousWasSpace = false;
            }

            if (builder.Length > 0)
            {
                AddOrMergeRun(normalized, new ParsedRun(builder.ToString(), run.IsBold, run.IsItalic));
            }
        }

        return normalized;
    }

    private static void AddOrMergeRun(List<ParsedRun> runs, ParsedRun next)
    {
        if (runs.Count > 0 &&
            runs[^1].IsBold == next.IsBold &&
            runs[^1].IsItalic == next.IsItalic)
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + next.Text };
            return;
        }

        runs.Add(next);
    }

    private sealed record ParsedRun(string Text, bool IsBold, bool IsItalic);
}
