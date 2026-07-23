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
using Avalonia.Input.Platform;
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

                case "referenceScrolled":
                    var scrolledReference = root.TryGetProperty("ref", out var scrolledRefElement)
                        ? scrolledRefElement.GetString()
                        : string.Empty;
                    ConfirmReaderReferenceScroll(state, scrolledReference);
                    break;

                case "dictionaryClicked":
                    var dictionaryReference = root.TryGetProperty("ref", out var dictionaryRefElement)
                        ? dictionaryRefElement.GetString()
                        : string.Empty;
                    var dictionaryWord = root.TryGetProperty("word", out var dictionaryWordElement)
                        ? dictionaryWordElement.GetString()
                        : string.Empty;
                    PixelPoint? dictionaryAnchor = null;
                    if (root.TryGetProperty("clientX", out var clientXElement) &&
                        clientXElement.TryGetDouble(out var clientX) &&
                        root.TryGetProperty("clientY", out var clientYElement) &&
                        clientYElement.TryGetDouble(out var clientY) &&
                        state.ReaderWebView is not null)
                    {
                        try
                        {
                            dictionaryAnchor = state.ReaderWebView.PointToScreen(new Point(clientX, clientY));
                        }
                        catch (InvalidOperationException)
                        {
                            // WebView may not be attached to a visual tree yet.
                        }
                    }

                    SelectReaderRowFromWebReference(state, dictionaryReference);
                    ShowDictionaryEntry(dictionaryWord, dictionaryReference, dictionaryAnchor);
                    break;

                case "copyClicked":
                    var copyText = root.TryGetProperty("text", out var copyTextElement)
                        ? copyTextElement.GetString()
                        : string.Empty;
                    _ = CopyReaderWebSelectionAsync(copyText);
                    break;

                case "switchTab":
                    var switchDirection = root.TryGetProperty("direction", out var directionElement) &&
                        directionElement.TryGetInt32(out var parsedDirection)
                        ? parsedDirection
                        : 0;
                    if (switchDirection != 0)
                    {
                        SelectAdjacentCenterTab(switchDirection);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed browser messages; the WebView is a rendering surface, not trusted app state.
        }
    }

    private async Task CopyReaderWebSelectionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
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

    private void ConfirmReaderReferenceScroll(ReaderTabState state, string? reference)
    {
        var pendingReference = state.PendingExactReferenceWithinWork;
        if (string.IsNullOrWhiteSpace(pendingReference) || string.IsNullOrWhiteSpace(reference))
        {
            return;
        }

        var row = state.ReaderRows
            .Where(candidate => !candidate.IsChapterHeading)
            .FirstOrDefault(candidate => string.Equals(
                GetReaderRowWebReference(state, candidate),
                reference,
                StringComparison.Ordinal));
        var isTargetRow = row is not null &&
            (IsReaderReferenceMatch(row.Primary?.Reference, pendingReference) ||
             IsReaderReferenceMatch(row.Translation?.Reference, pendingReference) ||
             string.Equals(row.ChapterKey, pendingReference, StringComparison.OrdinalIgnoreCase));
        if (!isTargetRow)
        {
            return;
        }

        _ = ClearPendingReaderReferenceAfterConfirmationAsync(
            state,
            pendingReference,
            state.ReaderScrollRestoreVersion);
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

            .reader-row.search-hit {
                background: #FFF4B8;
                border-radius: 4px;
            }

            .reader-row.search-hit mark {
                background: #FFE066;
                border-radius: 3px;
                box-decoration-break: clone;
                -webkit-box-decoration-break: clone;
                padding: 0 2px;
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

            .stndr-context-menu {
                position: fixed;
                display: none;
                min-width: 180px;
                padding: 6px;
                background: #fff;
                border: 1px solid #D0D5DD;
                border-radius: 8px;
                box-shadow: 0 8px 24px rgba(16, 24, 40, 0.2);
                z-index: 2147483647;
                font-family: var(--english-font);
                font-size: 13px;
            }

            .stndr-context-menu.open {
                display: block;
            }

            .stndr-context-menu button {
                display: block;
                width: 100%;
                border: 0;
                background: transparent;
                padding: 8px 10px;
                text-align: left;
                border-radius: 6px;
                color: #101828;
                cursor: pointer;
            }

            .stndr-context-menu button:hover {
                background: #F2F4F7;
            }

            .stndr-context-menu button:disabled {
                color: #98A2B3;
                cursor: default;
            }

            .stndr-context-menu .separator {
                margin: 4px 0;
                border-top: 1px solid #EAECF0;
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

        var highlightReference = NormalizeReaderUnitReference(state.SearchHighlightReferenceWithinWork);
        var isSearchHit = !string.IsNullOrWhiteSpace(highlightReference) &&
            (string.Equals(NormalizeReaderUnitReference(row.Primary?.Reference ?? string.Empty), highlightReference, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(NormalizeReaderUnitReference(row.Translation?.Reference ?? string.Empty), highlightReference, StringComparison.OrdinalIgnoreCase));
        var searchTerms = isSearchHit ? state.SearchHighlightTerms : new List<string>();
        if (isSearchHit)
        {
            className.Append(" search-hit");
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
                lineClass: "single-line",
                highlightTerms: searchTerms);
        }
        else if (state.DisplayMode is ReaderDisplayMode.SideBySide or ReaderDisplayMode.TranslationSideBySide)
        {
            builder.AppendLine("<div class=\"side-layout\">");
            builder.Append("<div class=\"primary-side\">");
            AppendReaderWebTextBlock(builder, row.Primary?.Text ?? string.Empty, primaryIsHebrew, state.HebrewMarksMode, searchTerms);
            builder.AppendLine("</div>");
            builder.Append("<div class=\"segment-label center-label\">");
            builder.Append(WebUtility.HtmlEncode(FirstNonEmpty(
                FormatSegmentLabel(row.Primary, primaryIsHebrew),
                FormatSegmentLabel(row.Translation, false))));
            builder.AppendLine("</div>");
            builder.Append("<div class=\"translation-side\">");
            AppendReaderWebTextBlock(builder, row.Translation?.Text ?? string.Empty, false, state.HebrewMarksMode, searchTerms);
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
                lineClass: "stacked-line",
                highlightTerms: searchTerms);
            AppendReaderWebLabeledLine(
                builder,
                row.Translation?.Text ?? string.Empty,
                false,
                state.HebrewMarksMode,
                leftLabel: string.Empty,
                rightLabel: FormatSegmentLabel(row.Translation, false),
                lineClass: "stacked-line",
                highlightTerms: searchTerms);
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
        string lineClass,
        IReadOnlyList<string>? highlightTerms = null)
    {
        builder.Append("<div class=\"");
        builder.Append(lineClass);
        builder.AppendLine("\">");
        builder.Append("<div class=\"segment-label\">");
        builder.Append(WebUtility.HtmlEncode(leftLabel));
        builder.AppendLine("</div>");
        AppendReaderWebTextBlock(builder, text, isHebrew, hebrewMarksMode, highlightTerms);
        builder.Append("<div class=\"segment-label\">");
        builder.Append(WebUtility.HtmlEncode(rightLabel));
        builder.AppendLine("</div>");
        builder.AppendLine("</div>");
    }

    private void AppendReaderWebTextBlock(
        StringBuilder builder,
        string text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode,
        IReadOnlyList<string>? highlightTerms = null)
    {
        builder.Append("<div class=\"text-block ");
        builder.Append(isHebrew ? "hebrew" : "english");
        builder.Append("\">");
        builder.Append(SanitizeReaderHtmlForWeb(text, isHebrew, hebrewMarksMode, highlightTerms));
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

                const collapseWhitespace = (text) => (text || '').replace(/\s+/g, ' ').trim();
                const extractWordFromText = (text) => {
                    const normalized = collapseWhitespace(text);
                    if (!normalized) {
                        return '';
                    }

                    const tokens = normalized.split(/\s+/).filter(Boolean);
                    return tokens.length > 0 ? tokens[0] : normalized;
                };

                const getWordAtPoint = (clientX, clientY) => {
                    let node = null;
                    let offset = 0;
                    if (document.caretPositionFromPoint) {
                        const position = document.caretPositionFromPoint(clientX, clientY);
                        if (position) {
                            node = position.offsetNode;
                            offset = position.offset;
                        }
                    } else if (document.caretRangeFromPoint) {
                        const range = document.caretRangeFromPoint(clientX, clientY);
                        if (range) {
                            node = range.startContainer;
                            offset = range.startOffset;
                        }
                    }

                    if (!node || node.nodeType !== Node.TEXT_NODE) {
                        return '';
                    }

                    const text = node.textContent || '';
                    if (!text) {
                        return '';
                    }

                    const separators = /[\s.,;:!?()[\]{}"'`<>\/\\|]+/;
                    let start = Math.min(offset, text.length);
                    let end = Math.min(offset, text.length);
                    while (start > 0 && !separators.test(text[start - 1])) {
                        start--;
                    }

                    while (end < text.length && !separators.test(text[end])) {
                        end++;
                    }

                    return collapseWhitespace(text.slice(start, end));
                };

                const contextState = {
                    ref: '',
                    selectedText: '',
                    clickedWord: '',
                    clientX: 0,
                    clientY: 0
                };

                const menu = document.createElement('div');
                menu.className = 'stndr-context-menu';
                menu.innerHTML = `
                    <button type="button" data-menu-action="copy">Copy</button>
                    <button type="button" data-menu-action="print">Print</button>
                    <button type="button" data-menu-action="dictionary">Dictionary</button>
                    <div class="separator" role="separator"></div>
                    <button type="button" data-menu-action="more-tools">More tools</button>
                `;
                document.body.appendChild(menu);

                const hideMenu = () => {
                    menu.classList.remove('open');
                };

                const showMenu = (clientX, clientY) => {
                    menu.classList.add('open');
                    const width = menu.offsetWidth || 180;
                    const height = menu.offsetHeight || 160;
                    const maxX = Math.max(8, window.innerWidth - width - 8);
                    const maxY = Math.max(8, window.innerHeight - height - 8);
                    menu.style.left = `${Math.max(8, Math.min(clientX, maxX))}px`;
                    menu.style.top = `${Math.max(8, Math.min(clientY, maxY))}px`;
                };

                document.addEventListener('contextmenu', (event) => {
                    const row = event.target.closest('.reader-row');
                    if (!row) {
                        hideMenu();
                        return;
                    }

                    event.preventDefault();
                    selectRow(row);
                    contextState.ref = row.dataset.ref || '';
                    send({ type: 'rowSelected', ref: contextState.ref });
                    const selection = window.getSelection();
                    contextState.selectedText = selection && !selection.isCollapsed
                        ? collapseWhitespace(selection.toString())
                        : '';
                    contextState.clickedWord = getWordAtPoint(event.clientX, event.clientY);
                    contextState.clientX = event.clientX;
                    contextState.clientY = event.clientY;
                    const copyButton = menu.querySelector('[data-menu-action="copy"]');
                    if (copyButton) {
                        copyButton.disabled = !contextState.selectedText;
                    }
                    showMenu(event.clientX, event.clientY);
                });

                menu.addEventListener('click', async (event) => {
                    const action = event.target.closest('[data-menu-action]')?.dataset.menuAction;
                    if (!action) {
                        return;
                    }

                    hideMenu();
                    switch (action) {
                        case 'copy':
                            send({ type: 'copyClicked', text: contextState.selectedText });
                            break;
                        case 'print':
                            window.print();
                            break;
                        case 'more-tools':
                            break;
                        case 'dictionary':
                            send({
                                type: 'dictionaryClicked',
                                ref: contextState.ref,
                                word: extractWordFromText(contextState.selectedText) || contextState.clickedWord || contextState.selectedText,
                                clientX: contextState.clientX,
                                clientY: contextState.clientY
                            });
                            break;
                    }
                });

                document.addEventListener('pointerdown', (event) => {
                    if (menu.classList.contains('open') && !menu.contains(event.target)) {
                        hideMenu();
                    }
                });

                document.addEventListener('scroll', hideMenu, { passive: true });
                window.addEventListener('blur', hideMenu);
                document.addEventListener('keydown', (event) => {
                    if (event.key === 'Escape') {
                        hideMenu();
                    }

                    // Native WebView focus does not bubble keys to Avalonia; forward tab shortcuts.
                    if (event.ctrlKey && !event.altKey && !event.shiftKey &&
                        (event.key === 'PageDown' || event.key === 'PageUp')) {
                        event.preventDefault();
                        send({
                            type: 'switchTab',
                            direction: event.key === 'PageDown' ? 1 : -1
                        });
                    }
                });

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
                    window.requestAnimationFrame(() => {
                        window.requestAnimationFrame(() => send({
                            type: 'referenceScrolled',
                            ref: reference
                        }));
                    });
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

        var restoreVersion = state.ReaderScrollRestoreVersion;
        state.IsApplyingWebScrollRestore = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsActiveReaderState(state) && state.ReaderScrollRestoreVersion == restoreVersion)
            {
                RestoreReaderWebScroll(state, restoreVersion);
            }
        }, DispatcherPriority.Background);
    }

    private void RestoreReaderWebScroll(ReaderTabState state)
    {
        RestoreReaderWebScroll(state, state.ReaderScrollRestoreVersion);
    }

    private void RestoreReaderWebScroll(ReaderTabState state, int restoreVersion)
    {
        RestoreReaderWebScroll(state, 0, restoreVersion);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 150, restoreVersion);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 450, restoreVersion);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 900, restoreVersion);
        _ = RestoreReaderWebScrollAfterDelayAsync(state, 1400, restoreVersion);
    }

    private async Task RestoreReaderWebScrollAfterDelayAsync(
        ReaderTabState state,
        int delayMilliseconds,
        int restoreVersion)
    {
        await Task.Delay(delayMilliseconds);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsActiveReaderState(state) && state.ReaderScrollRestoreVersion == restoreVersion)
            {
                RestoreReaderWebScroll(state, delayMilliseconds, restoreVersion);
            }
        });
    }

    private void RestoreReaderWebScroll(
        ReaderTabState state,
        int delayMilliseconds,
        int restoreVersion)
    {
        if (state.ReaderScrollRestoreVersion != restoreVersion)
        {
            return;
        }

        var targetReference = !string.IsNullOrWhiteSpace(state.PendingExactReferenceWithinWork)
            ? state.PendingExactReferenceWithinWork
            : state.SearchHighlightReferenceWithinWork;
        if (!string.IsNullOrWhiteSpace(targetReference))
        {
            ScrollReaderToExactReference(state, targetReference);
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

        state.SearchHighlightReferenceWithinWork = string.Empty;
        state.SearchHighlightTerms.Clear();
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

    private static string SanitizeReaderHtmlForWeb(
        string? text,
        bool isHebrew,
        HebrewMarksMode hebrewMarksMode,
        IReadOnlyList<string>? highlightTerms = null)
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

                builder.Append(EncodeReaderWebSegment(segment, highlightTerms));
            }

            position += length;
        }

        return builder.ToString();
    }

    private static string EncodeReaderWebSegment(string segment, IReadOnlyList<string>? highlightTerms)
    {
        if (string.IsNullOrEmpty(segment) || highlightTerms is null || highlightTerms.Count == 0)
        {
            return WebUtility.HtmlEncode(segment).Replace("\n", "<br>");
        }

        var spans = FindReaderHighlightSpans(segment, highlightTerms);
        if (spans.Count == 0)
        {
            return WebUtility.HtmlEncode(segment).Replace("\n", "<br>");
        }

        var builder = new StringBuilder(segment.Length + spans.Count * 16);
        var position = 0;
        foreach (var span in spans)
        {
            if (span.Start > position)
            {
                builder.Append(WebUtility.HtmlEncode(segment[position..span.Start]).Replace("\n", "<br>"));
            }

            builder.Append("<mark>");
            builder.Append(WebUtility.HtmlEncode(segment.Substring(span.Start, span.Length)).Replace("\n", "<br>"));
            builder.Append("</mark>");
            position = span.Start + span.Length;
        }

        if (position < segment.Length)
        {
            builder.Append(WebUtility.HtmlEncode(segment[position..]).Replace("\n", "<br>"));
        }

        return builder.ToString();
    }

    private static List<(int Start, int Length)> FindReaderHighlightSpans(
        string segment,
        IReadOnlyList<string> highlightTerms)
    {
        var normalizedBuilder = new StringBuilder(segment.Length);
        var originalIndexes = new List<int>(segment.Length);
        var previousWasSpace = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var character = segment[i];
            if (ShouldSuppressHebrewMarkForWeb(character, HebrewMarksMode.TextOnly))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                normalizedBuilder.Append(NormalizeFinalHebrewLetterForSearch(character));
                originalIndexes.Add(i);
                previousWasSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character) && !previousWasSpace)
            {
                normalizedBuilder.Append(' ');
                originalIndexes.Add(i);
                previousWasSpace = true;
            }
        }

        var normalized = normalizedBuilder.ToString().Trim();
        var spans = new List<(int Start, int Length)>();
        foreach (var term in highlightTerms
            .Select(term => NormalizeReaderHighlightTerm(term))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length))
        {
            var searchStart = 0;
            while (searchStart < normalized.Length)
            {
                var index = normalized.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                if (index < originalIndexes.Count && index + term.Length - 1 < originalIndexes.Count)
                {
                    var originalStart = originalIndexes[index];
                    var originalEnd = originalIndexes[index + term.Length - 1] + 1;
                    if (!spans.Any(span => originalStart < span.Start + span.Length && originalEnd > span.Start))
                    {
                        spans.Add((originalStart, originalEnd - originalStart));
                    }
                }

                searchStart = index + Math.Max(1, term.Length);
            }
        }

        return spans
            .OrderBy(span => span.Start)
            .ToList();
    }

    private static string NormalizeReaderHighlightTerm(string term)
    {
        var builder = new StringBuilder(term.Length);
        var previousWasSpace = false;
        foreach (var character in term)
        {
            if (ShouldSuppressHebrewMarkForWeb(character, HebrewMarksMode.TextOnly))
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(NormalizeFinalHebrewLetterForSearch(character));
                previousWasSpace = false;
            }
            else if (char.IsWhiteSpace(character) && !previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static char NormalizeFinalHebrewLetterForSearch(char character)
    {
        return character switch
        {
            '\u05da' => '\u05db',
            '\u05dd' => '\u05de',
            '\u05df' => '\u05e0',
            '\u05e3' => '\u05e4',
            '\u05e5' => '\u05e6',
            _ => character
        };
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
