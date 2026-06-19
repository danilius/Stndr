using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stndr;

public partial class MainWindow
{
    private NativeWebView CreateReaderWebView(ReaderTabState state)
    {
        var webView = new NativeWebView
        {
            Background = Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        webView.WebMessageReceived += (_, e) => HandleReaderWebMessage(state, e.Body);
        webView.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                return;
            }

            if (!state.HasAppliedInitialWebScroll)
            {
                state.HasAppliedInitialWebScroll = true;
                RestoreReaderWebScroll(state);
            }
        };

        return webView;
    }

    private void RenderReaderWebView(ReaderTabState state)
    {
        if (state.ReaderWebView is null)
        {
            return;
        }

        var html = BuildReaderWebDocument(state);
        state.HasAppliedInitialWebScroll = false;
        state.IsApplyingWebScrollRestore = true;
        NavigateReaderWebView(state.ReaderWebView, html);
    }

    private void NavigateReaderWebView(NativeWebView webView, string html)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Stndr", "ReaderWebView");
        Directory.CreateDirectory(folder);
        var filePath = Path.Combine(folder, $"reader-{Guid.NewGuid():N}.html");
        File.WriteAllText(filePath, html, Encoding.UTF8);
        webView.Navigate(new Uri(filePath));
    }

    private void HandleReaderWebMessage(ReaderTabState state, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : string.Empty;

            switch (type)
            {
                case "rowSelected":
                case "selectionChanged":
                    var reference = root.TryGetProperty("ref", out var refElement)
                        ? refElement.GetString()
                        : string.Empty;
                    SelectReaderRowFromWebReference(state, reference);
                    break;

                case "scroll":
                    if (root.TryGetProperty("offset", out var offsetElement) &&
                        offsetElement.TryGetDouble(out var offset))
                    {
                        TrackReaderWebScroll(state, offset);
                    }

                    if (root.TryGetProperty("chapter", out var chapterElement))
                    {
                        UpdateReaderChapterHeaderFromWeb(state, chapterElement.GetString());
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed browser messages; the WebView is a rendering surface, not trusted app state.
        }
    }

    private void SelectReaderRowFromWebReference(ReaderTabState state, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        var row = state.ReaderRows
            .Where(candidate => !candidate.IsChapterHeading)
            .FirstOrDefault(candidate =>
                string.Equals(GetReaderRowWebReference(state, candidate), reference, StringComparison.Ordinal));
        if (row is null || ReferenceEquals(state.SelectedReaderRow, row))
        {
            return;
        }

        OnReaderParagraphSelectionChanged(state, row);
    }

    private string BuildReaderWebDocument(ReaderTabState state)
    {
        var primaryIsHebrew = SefariaLibraryService.IsHebrew(state.Primary);
        var title = WebUtility.HtmlEncode(FormatTitle(state.Primary.Title, state.Primary.HebrewTitle));
        var displayClass = state.DisplayMode switch
        {
            ReaderDisplayMode.SideBySide => "side-by-side",
            ReaderDisplayMode.TranslationSideBySide => "translation-side-by-side",
            ReaderDisplayMode.TranslationBelow => "translation-below",
            _ => "primary-only"
        };

        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html>");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine($"<title>{title}</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(BuildReaderWebCss(primaryIsHebrew));
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine($"<body class=\"{displayClass}\">");
        builder.AppendLine("<main id=\"reader\">");

        foreach (var row in state.ReaderRows)
        {
            AppendReaderWebRow(builder, state, row, primaryIsHebrew);
        }

        builder.AppendLine("</main>");
        builder.AppendLine("<script>");
        builder.AppendLine(BuildReaderWebScript());
        builder.AppendLine("</script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private string BuildReaderWebCss(bool primaryIsHebrew)
    {
        var englishFont = CssString(GetSelectedEnglishFontFamily());
        var hebrewFont = CssString(GetSelectedHebrewFontFamily());
        var englishSize = GetSelectedEnglishFontSize().ToString(CultureInfo.InvariantCulture);
        var hebrewSize = GetSelectedHebrewFontSize().ToString(CultureInfo.InvariantCulture);
        var contentWidth = EstimateReaderWidth(GetSingleLanguageReaderColumnLetters(), Math.Max(GetSelectedHebrewFontSize(), GetSelectedEnglishFontSize()))
            .ToString(CultureInfo.InvariantCulture);
        var dualWidth = EstimateReaderWidth(GetDualLanguageReaderColumnLetters(), Math.Max(GetSelectedHebrewFontSize(), GetSelectedEnglishFontSize()))
            .ToString(CultureInfo.InvariantCulture);

        return $$"""
            :root {
                color-scheme: light;
                --selection: rgba(90, 169, 230, 0.28);
                --selected-row: rgba(90, 169, 230, 0.08);
                --text: #101828;
                --muted: #98A2B3;
                --english-font: {{englishFont}};
                --hebrew-font: {{hebrewFont}};
                --english-size: {{englishSize}}px;
                --hebrew-size: {{hebrewSize}}px;
                --single-width: {{contentWidth}}px;
                --dual-width: {{dualWidth}}px;
            }

            html, body {
                margin: 0;
                min-height: 100%;
                background: #fff;
                color: var(--text);
            }

            body {
                font-family: var(--english-font);
                overflow-y: auto;
                user-select: text;
            }

            ::selection {
                background: var(--selection);
                color: #000;
            }

            #reader {
                box-sizing: border-box;
                padding: 16px 96px 32px 32px;
            }

            .chapter-heading {
                color: #344054;
                font-family: var(--english-font);
                font-size: 18px;
                font-weight: 600;
                margin: 18px 0 18px;
                text-align: center;
            }

            .reader-row {
                box-sizing: border-box;
                margin: 0 auto 16px;
                max-width: var(--single-width);
                padding: 0;
                width: min(100%, var(--single-width));
            }

            body.side-by-side .reader-row,
            body.translation-side-by-side .reader-row,
            body.translation-below .reader-row {
                max-width: var(--dual-width);
                width: min(100%, var(--dual-width));
            }

            .reader-row.selected {
                background: var(--selected-row);
            }

            .reader-row.aliyah {
                background: var(--aliyah-bg);
                margin-bottom: 0;
                padding-block: 2px;
            }

            .reader-row.aliyah-start {
                border-start-start-radius: 4px;
                border-start-end-radius: 4px;
                margin-top: 10px;
                padding-top: 8px;
            }

            .reader-row.aliyah-end {
                border-end-start-radius: 4px;
                border-end-end-radius: 4px;
                margin-bottom: 16px;
                padding-bottom: 8px;
            }

            .aliyah-heading {
                color: #344054;
                font-family: var(--english-font);
                font-size: 13px;
                font-weight: 600;
                margin: 0 16px 4px;
                text-align: center;
                user-select: none;
            }

            .single-line,
            .stacked-line {
                align-items: start;
                display: grid;
                gap: 10px;
                grid-template-columns: 32px minmax(0, 1fr) 32px;
            }

            .side-layout {
                align-items: start;
                display: grid;
                grid-template-columns: minmax(0, 1fr) 44px minmax(0, 1fr);
            }

            .primary-side,
            .translation-side {
                grid-row: 1;
                min-width: 0;
                width: 100%;
            }

            .center-label {
                grid-row: 1;
            }

            .text-block {
                box-sizing: border-box;
                line-height: 1.75;
                padding: 8px 16px;
                white-space: normal;
                width: 100%;
            }

            .text-block.hebrew {
                direction: rtl;
                font-family: var(--hebrew-font);
                font-size: var(--hebrew-size);
                padding-inline-start: 24px;
                text-align: right;
                unicode-bidi: plaintext;
            }

            .text-block.english {
                direction: ltr;
                font-family: var(--english-font);
                font-size: var(--english-size);
                text-align: left;
            }

            .segment-label {
                color: var(--muted);
                font-family: var(--english-font);
                font-size: 12px;
                line-height: 1.3;
                margin-top: 11px;
                text-align: center;
                user-select: none;
            }

            .translation-below .stacked-line + .stacked-line {
                margin-top: 8px;
            }

            body.translation-side-by-side .primary-side {
                grid-column: 3;
            }

            body.translation-side-by-side .translation-side {
                grid-column: 1;
            }

            body.translation-side-by-side .center-label {
                grid-column: 2;
            }

            b, strong {
                font-weight: 700;
            }

            i, em {
                font-style: italic;
            }
            """;
    }

    private void AppendReaderWebRow(
        StringBuilder builder,
        ReaderTabState state,
        ReaderDisplayRow row,
        bool primaryIsHebrew)
    {
        if (row.IsChapterHeading)
        {
            var headingChapterKey = WebUtility.HtmlEncode(row.ChapterKey);
            builder.Append("<section class=\"chapter-heading\" data-chapter=\"");
            builder.Append(headingChapterKey);
            builder.Append("\">");
            builder.Append(WebUtility.HtmlEncode(row.ChapterHeading));
            builder.AppendLine("</section>");
            return;
        }

        var reference = WebUtility.HtmlEncode(GetReaderRowWebReference(state, row));
        var sourceReference = FirstNonEmpty(row.Primary?.Reference, row.Translation?.Reference);
        var rowChapterKey = WebUtility.HtmlEncode(row.ChapterKey);
        var aliyah = state.ShowAliyot && GetSelectedTorahSedra(state) is { } sedra
            ? GetAliyahForReaderReference(sedra, sourceReference)
            : null;
        var isAliyahStart = aliyah is not null && IsAliyahStart(aliyah, sourceReference);
        var isAliyahEnd = aliyah is not null && IsAliyahEnd(aliyah, sourceReference);
        var className = new StringBuilder("reader-row");
        if (aliyah is not null)
        {
            className.Append(" aliyah");
        }

        if (isAliyahStart)
        {
            className.Append(" aliyah-start");
        }

        if (isAliyahEnd)
        {
            className.Append(" aliyah-end");
        }

        builder.Append("<section class=\"");
        builder.Append(className);
        builder.Append("\" data-ref=\"");
        builder.Append(reference);
        builder.Append("\" data-chapter=\"");
        builder.Append(rowChapterKey);
        builder.Append("\"");
        if (aliyah is not null)
        {
            builder.Append(" data-aliyah=\"");
            builder.Append(aliyah.Number.ToString(CultureInfo.InvariantCulture));
            builder.Append("\" style=\"--aliyah-bg:");
            builder.Append(GetAliyahCssColor(aliyah.Number));
            builder.Append("\"");
        }

        builder.AppendLine(">");
        if (isAliyahStart && aliyah is not null)
        {
            builder.Append("<div class=\"aliyah-heading\">");
            builder.Append(WebUtility.HtmlEncode(FormatAliyahHeading(aliyah.Number)));
            builder.AppendLine("</div>");
        }

        var showTranslation = row.Translation is not null && state.DisplayMode != ReaderDisplayMode.PrimaryOnly;
        if (!showTranslation)
        {
            AppendReaderWebLabeledLine(
                builder,
                row.Primary?.Text ?? string.Empty,
                primaryIsHebrew,
                state.HebrewMarksMode,
                leftLabel: string.Empty,
                rightLabel: FormatSegmentLabel(row.Primary, primaryIsHebrew),
                lineClass: "single-line");
        }
        else if (state.DisplayMode is ReaderDisplayMode.SideBySide or ReaderDisplayMode.TranslationSideBySide)
        {
            builder.AppendLine("<div class=\"side-layout\">");
            builder.Append("<div class=\"primary-side\">");
            AppendReaderWebTextBlock(builder, row.Primary?.Text ?? string.Empty, primaryIsHebrew, state.HebrewMarksMode);
            builder.AppendLine("</div>");
            builder.Append("<div class=\"segment-label center-label\">");
            builder.Append(WebUtility.HtmlEncode(FirstNonEmpty(
                FormatSegmentLabel(row.Primary, primaryIsHebrew),
                FormatSegmentLabel(row.Translation, false))));
            builder.AppendLine("</div>");
            builder.Append("<div class=\"translation-side\">");
            AppendReaderWebTextBlock(builder, row.Translation?.Text ?? string.Empty, false, state.HebrewMarksMode);
            builder.AppendLine("</div>");
            builder.AppendLine("</div>");
        }
        else
        {
            AppendReaderWebLabeledLine(
                builder,
                row.Primary?.Text ?? string.Empty,
                primaryIsHebrew,
                state.HebrewMarksMode,
                leftLabel: FormatSegmentLabel(row.Primary, primaryIsHebrew),
                rightLabel: string.Empty,
                lineClass: "stacked-line");
            AppendReaderWebLabeledLine(
                builder,
                row.Translation?.Text ?? string.Empty,
                false,
                state.HebrewMarksMode,
                leftLabel: string.Empty,
                rightLabel: FormatSegmentLabel(row.Translation, false),
                lineClass: "stacked-line");
        }

        builder.AppendLine("</section>");
    }

    private void AppendReaderWebLabeledLine(
        StringBuilder builder,
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode,
        string leftLabel,
        string rightLabel,
        string lineClass)
    {
        builder.Append("<div class=\"");
        builder.Append(lineClass);
        builder.AppendLine("\">");
        builder.Append("<div class=\"segment-label\">");
        builder.Append(WebUtility.HtmlEncode(leftLabel));
        builder.AppendLine("</div>");
        AppendReaderWebTextBlock(builder, text, isHebrew, hebrewMarksMode);
        builder.Append("<div class=\"segment-label\">");
        builder.Append(WebUtility.HtmlEncode(rightLabel));
        builder.AppendLine("</div>");
        builder.AppendLine("</div>");
    }

    private void AppendReaderWebTextBlock(
        StringBuilder builder,
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode)
    {
        builder.Append("<div class=\"text-block ");
        builder.Append(isHebrew ? "hebrew" : "english");
        builder.Append("\">");
        builder.Append(SanitizeReaderHtmlForWeb(text, isHebrew, hebrewMarksMode));
        builder.AppendLine("</div>");
    }

    private string BuildReaderWebScript()
    {
        return """
            (function () {
                const send = (message) => {
                    if (typeof invokeCSharpAction === 'function') {
                        invokeCSharpAction(JSON.stringify(message));
                    }
                };

                const selectRow = (row) => {
                    document.querySelectorAll('.reader-row.selected')
                        .forEach((selected) => selected.classList.remove('selected'));
                    if (row) {
                        row.classList.add('selected');
                    }
                };

                document.addEventListener('click', (event) => {
                    const row = event.target.closest('.reader-row');
                    if (!row) {
                        return;
                    }

                    selectRow(row);
                    send({ type: 'rowSelected', ref: row.dataset.ref || '' });
                });

                let selectionTimer = 0;
                document.addEventListener('selectionchange', () => {
                    window.clearTimeout(selectionTimer);
                    selectionTimer = window.setTimeout(() => {
                        const selection = window.getSelection();
                        if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
                            return;
                        }

                        const range = selection.getRangeAt(0);
                        const start = range.startContainer.parentElement?.closest('.reader-row');
                        const end = range.endContainer.parentElement?.closest('.reader-row');
                        const row = start || end;
                        if (!row) {
                            return;
                        }

                        selectRow(row);
                        send({
                            type: 'selectionChanged',
                            ref: row.dataset.ref || '',
                            startRef: start?.dataset.ref || '',
                            endRef: end?.dataset.ref || '',
                            text: selection.toString()
                        });
                    }, 80);
                });

                const getVisibleChapter = () => {
                    const candidates = Array.from(document.querySelectorAll('[data-chapter]'));
                    let best = '';
                    let bestDistance = Number.POSITIVE_INFINITY;
                    for (const candidate of candidates) {
                        const rect = candidate.getBoundingClientRect();
                        if (rect.bottom < 0 || rect.top > window.innerHeight) {
                            continue;
                        }

                        const distance = rect.top <= 0 ? Math.abs(rect.top) * 0.1 : rect.top;
                        if (distance < bestDistance) {
                            bestDistance = distance;
                            best = candidate.dataset.chapter || '';
                        }
                    }

                    return best;
                };

                let scrollTimer = 0;
                document.addEventListener('scroll', () => {
                    window.clearTimeout(scrollTimer);
                    scrollTimer = window.setTimeout(() => {
                        send({
                            type: 'scroll',
                            offset: window.scrollY || 0,
                            chapter: getVisibleChapter()
                        });
                    }, 150);
                }, { passive: true });

                window.stndrScrollToOffset = (offset) => {
                    window.scrollTo({ top: Math.max(0, Number(offset) || 0), left: 0 });
                };

                window.stndrScrollToRef = (reference) => {
                    const row = document.querySelector(`.reader-row[data-ref="${CSS.escape(reference)}"]`);
                    if (!row) {
                        return;
                    }

                    selectRow(row);
                    row.scrollIntoView({ block: 'start', inline: 'nearest' });
                };

                window.stndrScrollToChapter = (chapter) => {
                    const heading = document.querySelector(`.chapter-heading[data-chapter="${CSS.escape(chapter)}"]`);
                    const firstRow = document.querySelector(`.reader-row[data-chapter="${CSS.escape(chapter)}"]`);
                    const target = heading || firstRow;
                    if (!target) {
                        return;
                    }

                    if (firstRow) {
                        selectRow(firstRow);
                    }
                    target.scrollIntoView({ block: 'start', inline: 'nearest' });
                };
            })();
            """;
    }

    private void UpdateReaderChapterHeaderFromWeb(ReaderTabState state, string? chapterKey)
    {
        if (string.IsNullOrWhiteSpace(chapterKey) ||
            string.Equals(state.CurrentChapterKey, chapterKey, StringComparison.Ordinal))
        {
            return;
        }

        var item = state.NavigationItems.FirstOrDefault(item =>
            string.Equals(item.Row.ChapterKey, chapterKey, StringComparison.Ordinal));
        UpdateReaderChapterHeader(state, item?.Row);
    }

    private static bool IsAliyahEnd(TorahAliyah aliyah, string reference)
    {
        return TryParseReaderReference(reference, out var verse) &&
            TryParseTorahRange(aliyah.Ref, out _, out var end) &&
            verse.CompareTo(end) == 0;
    }

    private static string GetAliyahCssColor(int aliyahNumber)
    {
        var colors = new[]
        {
            "#FFF3C4",
            "#DFF7E8",
            "#DDEBFF",
            "#F5E3FF",
            "#FFE3DF",
            "#E1F5F7",
            "#F1EAD8"
        };
        return colors[Math.Clamp(aliyahNumber - 1, 0, colors.Length - 1)];
    }

    private static string GetReaderRowWebReference(ReaderTabState state, ReaderDisplayRow row)
    {
        return FirstNonEmpty(
            BuildSefariaAnchorRef(state, row, preferTranslation: false),
            BuildSefariaAnchorRef(state, row, preferTranslation: true));
    }

    private void ScrollReaderWebViewToOffset(ReaderTabState state, double offset)
    {
        if (state.ReaderWebView is null)
        {
            state.IsApplyingWebScrollRestore = false;
            return;
        }

        state.IsApplyingWebScrollRestore = true;
        var script = $"window.stndrScrollToOffset({Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)});";
        _ = state.ReaderWebView.InvokeScript(script);
    }

    private void RestoreSelectedReaderWebScrollAfterTabSwitch()
    {
        if (_centerTabs?.SelectedItem is not TabItem selectedTab ||
            !_openReaderTabs.TryGetValue(selectedTab, out var state) ||
            state.ReaderWebView is null)
        {
            return;
        }

        state.IsApplyingWebScrollRestore = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsActiveReaderState(state))
            {
                RestoreReaderWebScroll(state);
            }
        }, DispatcherPriority.Loaded);
    }

    private void RestoreReaderWebScroll(ReaderTabState state)
    {
        RestoreReaderWebScroll(state, 0);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 150);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 450);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 900);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 1400);
    }

    private async Task RestoreReaderWebScrollAfterDelayAsync(ReaderTabState state, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsActiveReaderState(state))
            {
                RestoreReaderWebScroll(state, delayMilliseconds);
            }
        });
    }

    private void RestoreReaderWebScroll(ReaderTabState state, int delayMilliseconds)
    {
        if (!string.IsNullOrWhiteSpace(state.PendingExactReferenceWithinWork))
        {
            ScrollReaderToExactReference(state, state.PendingExactReferenceWithinWork);
            if (delayMilliseconds >= 1400)
            {
                state.PendingExactReferenceWithinWork = string.Empty;
                Dispatcher.UIThread.Post(() => state.IsApplyingWebScrollRestore = false, DispatcherPriority.Background);
            }

            return;
        }

        ScrollReaderWebViewToOffset(state, state.ReaderWebScrollOffset);
        if (delayMilliseconds >= 450)
        {
            Dispatcher.UIThread.Post(() => state.IsApplyingWebScrollRestore = false, DispatcherPriority.Background);
        }
    }

    private void TrackReaderWebScroll(ReaderTabState state, double offset)
    {
        if (state.IsApplyingWebScrollRestore || !IsActiveReaderState(state))
        {
            return;
        }

        ScheduleReadingPositionSave(state, offset);
        state.ReaderWebScrollOffset = offset;
    }

    private bool IsActiveReaderState(ReaderTabState state)
    {
        return _centerTabs?.SelectedItem is TabItem selectedTab &&
            _openReaderTabs.TryGetValue(selectedTab, out var selectedState) &&
            ReferenceEquals(selectedState, state);
    }

    private static void ScrollReaderWebViewToReference(ReaderTabState state, ReaderDisplayRow row)
    {
        if (state.ReaderWebView is null)
        {
            return;
        }

        if (row.IsChapterHeading)
        {
            ScrollReaderWebViewToChapter(state, row.ChapterKey);
            return;
        }

        var reference = GetReaderRowWebReference(state, row);
        if (string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        var script = $"window.stndrScrollToRef({JsonSerializer.Serialize(reference)});";
        _ = state.ReaderWebView.InvokeScript(script);
    }

    private static void ScrollReaderWebViewToChapter(ReaderTabState state, string chapterKey)
    {
        if (state.ReaderWebView is null || string.IsNullOrWhiteSpace(chapterKey))
        {
            return;
        }

        var script = $"window.stndrScrollToChapter({JsonSerializer.Serialize(chapterKey)});";
        _ = state.ReaderWebView.InvokeScript(script);
    }

    private static string CssString(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static string SanitizeReaderHtmlForWeb(string? text, bool isHebrew, HebrewMarksMode hebrewMarksMode)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var suppressedTags = new Stack<string>();
        var position = 0;

        while (position < text.Length)
        {
            if (text[position] == '<')
            {
                var tagEnd = text.IndexOf('>', position + 1);
                if (tagEnd >= 0)
                {
                    var tag = text.Substring(position + 1, tagEnd - position - 1);
                    AppendSafeReaderTag(builder, tag, suppressedTags);
                    position = tagEnd + 1;
                    continue;
                }
            }

            var nextTag = text.IndexOf('<', position);
            var length = nextTag < 0 ? text.Length - position : nextTag - position;
            if (suppressedTags.Count == 0)
            {
                var segment = WebUtility.HtmlDecode(text.Substring(position, length).Replace('\uFFFD', '?'));
                if (isHebrew)
                {
                    segment = ApplyHebrewMarksModeForWeb(segment, hebrewMarksMode);
                }

                builder.Append(WebUtility.HtmlEncode(segment).Replace("\n", "<br>"));
            }

            position += length;
        }

        return builder.ToString();
    }

    private static void AppendSafeReaderTag(StringBuilder builder, string tag, Stack<string> suppressedTags)
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
                builder.Append("<br>");
                break;
            case "p":
                if (isClosing)
                {
                    builder.Append("<br><br>");
                }
                break;
            case "div":
            case "blockquote":
                if (isClosing)
                {
                    builder.Append("<br>");
                }
                break;
            case "b":
            case "strong":
                builder.Append(isClosing ? "</strong>" : "<strong>");
                break;
            case "i":
            case "em":
                builder.Append(isClosing ? "</em>" : "<em>");
                break;
        }
    }

    private static string ApplyHebrewMarksModeForWeb(string text, HebrewMarksMode mode)
    {
        if (mode == HebrewMarksMode.NikkudAndCantillation)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!ShouldSuppressHebrewMarkForWeb(character, mode))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool ShouldSuppressHebrewMarkForWeb(char character, HebrewMarksMode mode)
    {
        var code = (int)character;
        var isCantillation = code >= 0x0591 && code <= 0x05AF;
        var isNikkud = (code >= 0x05B0 && code <= 0x05BC) ||
            code == 0x05C1 ||
            code == 0x05C2 ||
            code == 0x05C7;

        return mode switch
        {
            HebrewMarksMode.TextOnly => isCantillation || isNikkud,
            HebrewMarksMode.Nikkud => isCantillation,
            _ => false
        };
    }
}
