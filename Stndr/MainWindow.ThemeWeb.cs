using System.Globalization;

namespace Stndr;

public partial class MainWindow
{
    private static void AppendThemedChapterHeading(System.Text.StringBuilder builder, string heading, string chapterKey)
    {
        var parts = heading.Split(" / ", System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && (heading.StartsWith("Chapter ") || heading.StartsWith("פרק ")) && int.TryParse(chapterKey, out var number) && number > 0)
            parts = new[] { "פרק " + ToHebrewNumber(number), "Chapter " + number };
        foreach (var part in parts)
        {
            var hebrew = System.Linq.Enumerable.Any(part, c => c >= '\u0590' && c <= '\u05ff');
            builder.Append("<span class=\"").Append(hebrew ? "hebrew-heading" : "english-heading").Append("\">");
            builder.Append(System.Net.WebUtility.HtmlEncode(part)).Append("</span>");
        }
    }

    private static string BuildThemeReaderCss() => $$"""
        :root { color-scheme: {{(UiTheme.IsDark ? "dark" : "light")}}; }
        html, body { background: {{UiTheme.CssColor("Surface")}}; color: {{UiTheme.CssColor("Text")}}; }
        #reader { padding: {{UiTheme.Metric("ReaderPadding").ToString(CultureInfo.InvariantCulture)}}px 32px 40px; }
        .reader-row { margin-bottom: {{UiTheme.Metric("ReaderRowGap").ToString(CultureInfo.InvariantCulture)}}px; }
        .text-block { padding: 0 6px; }
        .text-block.hebrew { {{UiTheme.CssText("ReaderHebrew")}} padding-inline-start: 0; }
        .text-block.english { {{UiTheme.CssText("ReaderEnglish")}} }
        .translation-below .text-block.english { text-align: right; }
        .translation-below .stacked-line + .stacked-line { margin-top: 2px; }
        .segment-label { {{UiTheme.CssText("Reference")}} margin-top: 9px; }
        .chapter-heading { {{UiTheme.CssText("EnglishHeading")}} margin: 0 0 22px; }
        .chapter-heading .hebrew-heading { {{UiTheme.CssText("HebrewHeading")}} display: block; }
        .chapter-heading .english-heading { {{UiTheme.CssText("EnglishHeading")}} display: block; }
        .chapter-heading .hebrew-heading::after { content: ''; display: block; width: 64px; height: 1px; background: {{UiTheme.CssColor("Icon")}}; margin: 3px auto; }
        .aliyah-heading { {{UiTheme.CssText("Breadcrumb")}} }
        .annotation-marker { {{UiTheme.CssText("AnnotationMarker")}} }
        .stndr-context-menu, .annotation-note-popover { background: {{UiTheme.CssColor("Surface")}}; border-color: {{UiTheme.CssColor("Border")}}; {{UiTheme.CssText("Menu")}} }
        .stndr-context-menu button { {{UiTheme.CssText("Menu")}} }
        .stndr-context-menu button:hover { background: {{UiTheme.CssColor("Hover")}}; }
        .reader-row.search-hit { background: {{UiTheme.CssColor("SearchHit")}}; }
        .reader-row.search-hit mark { background: {{UiTheme.CssColor("SearchHighlight")}}; }
        mark.annotation-bookmark { border-bottom-color: {{UiTheme.CssColor("Icon")}}; }
        mark.annotation-note { border-bottom-color: {{UiTheme.CssColor("NoteMarker")}}; }
        .stndr-context-menu, .submenu-panel { box-shadow: 0 8px 24px {{UiTheme.CssColor("Shadow")}}; }
        a { color: {{UiTheme.CssColor("Link")}}; }
        * { scrollbar-color: {{UiTheme.CssColor("Border")}} {{UiTheme.CssColor("Surface")}}; }
        """;

    private static string BuildThemeSplitCss() => $$"""
        :root { color-scheme: {{(UiTheme.IsDark ? "dark" : "light")}}; }
        html, body { {{UiTheme.CssText("Body")}} background: {{UiTheme.CssColor("Surface")}}; }
        .hebrew, .text-block.hebrew { {{UiTheme.CssText("CommentaryHebrew")}} }
        .english, .text-block.english { {{UiTheme.CssText("CommentaryEnglish")}} }
        .reference, .ref { {{UiTheme.CssText("Reference")}} }
        h1 { {{UiTheme.CssText("ExpanderHeader")}} }
        button { {{UiTheme.CssText("Button")}} background: {{UiTheme.CssColor("Surface")}}; border-color: {{UiTheme.CssColor("Border")}}; }
        button:hover { background: {{UiTheme.CssColor("Hover")}}; }
        a { color: {{UiTheme.CssColor("Link")}}; }
        ::selection { background: {{UiTheme.CssColor("TextSelection")}}; color: {{UiTheme.CssColor("Text")}}; }
        """;
}
