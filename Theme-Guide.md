# UI themes

The default design is defined in `Stndr/Themes/warm.json`. On first launch, Stndr copies it to `theme.json` in your configured Data folder. Settings → Theme shows the location, opens the file, and reloads edits. Themes may contain only the properties you want to override; missing properties inherit the bundled warm theme. Invalid edits retain the last valid theme and display an error.

## Text categories

Each entry in `Text` has `FontFamily`, `HebrewFontFamily` (fallback for mixed-script native labels), `Size` (device-independent pixels), `Color` (a palette key or colour literal), `Weight` (100–900), and `LineHeight` (a multiplier).

Categories include Body, Button, MainButton, Input, LibraryItem, Navigation, ExpanderHeader, Tab, PageTitle, Breadcrumb, HebrewHeading, EnglishHeading, ReaderHebrew, ReaderEnglish, CommentaryHebrew, CommentaryEnglish, DictionaryHeadword, DictionaryDefinition, DictionaryDefinitionHebrew, SearchResult, SearchResultHebrew, SearchReference, Reference, ReferenceHebrew, Metadata, Status, AnnotationBody, AnnotationMarker, Menu, Tooltip, SectionHeading, and DialogTitle.

Font families follow the Settings screen for all interface text. Every category except the reader-content ones uses Body's `FontFamily` (the "Display font" setting) and HebrewUi's `FontFamily` (the "Hebrew display font" setting) for Hebrew. DictionaryHeadword, ReferenceHebrew and DictionaryDefinitionHebrew are Hebrew-first, so they use the Hebrew display font throughout. In those categories only `FontFamily` and `HebrewFontFamily` are ignored. Size, colour, weight and line height still apply per category. Reader-content categories keep their own fonts: ReaderHebrew, ReaderEnglish, CommentaryHebrew, CommentaryEnglish, HebrewHeading, EnglishHeading, SearchResult and SearchResultHebrew. Main-function button labels always use the Icon colour to match their icons.

For example:

```json
{
  "Version": 1,
  "Colors": {
    "Icon": "#6C5238",
    "Text": "#29241E",
    "Surface": "#FFFDF8"
  },
  "Text": {
    "ReaderHebrew": { "FontFamily": "David", "Size": 32, "Color": "Text" },
    "ReaderEnglish": { "FontFamily": "Georgia", "Size": 19 },
    "Reference": { "FontFamily": "Georgia", "Size": 13, "Color": "Icon" },
    "ExpanderHeader": { "FontFamily": "Georgia", "Size": 14, "Weight": 500 }
  },
  "Metrics": {
    "MainButtonHeight": 40,
    "ExpanderHeaderHeight": 38,
    "PageHeaderHeight": 64
  }
}
```

Fonts must be installed on the computer. Native labels also use HebrewFontFamily as a fallback. The default Hebrew face is David. Font settings in the Settings screen update the corresponding theme entries.

## Palette and layout

Colours use `#RRGGBB` or Avalonia's `#AARRGGBB` notation. Palette categories include surfaces, text, icons, borders, hover, pressed, focus, selection (`Selection` for the selected paragraph or navigation entry, `TextSelection` for highlighted text, which must be darker so a text selection stays visible inside a selected paragraph), links, status colours, annotation highlights, and the seven aliyah backgrounds. The reader converts alpha colours to CSS rgba automatically. A dark Surface selects dark native controls and browser colour scheme.

Settings > Theme > Colour scheme switches between the bundled schemes in `Themes/` (Warm parchment, Blue, Azure, Green). A scheme replaces only `Colors` in your theme file, so fonts, sizes and metrics are kept. To add a scheme, drop a JSON file with `Version`, `Name` and `Colors` into `Themes/`; missing colours fall back to Warm parchment.

Metrics cover toolbar/expander/page-header/control/tab heights, tab width, panel widths, icon size, corners, reader padding and paragraph spacing. Font/colour changes reload in native controls and the HTML reader. Some structural dimensions and existing font-based inline content are fully refreshed when a view is reopened.

Window resizing, selected book versions, title-language preferences, and reading mode still affect the visible layout; the theme does not change source text or translations. Pronunciation playback and Study Tools are not added.

## Validation and render comparison

Run `dotnet test Stndr.sln` and `node --test Stndr.Tests/ReaderViewportTests.cjs`.

The UI tests run in their own process to keep Avalonia's dispatcher isolated from the existing control unit tests. They use temporary settings and suppress startup/background services.

Set `STNDR_UI_ARTIFACTS` to an output directory to export shell, Dictionary, Advanced Search, Settings, dark-theme, and reader previews. Optionally set `STNDR_VISUAL_DATA` to an installed Stndr data folder to use real Genesis, Berakhot, and Siddur Edot HaMizrach text. Reading state remains in the test's temporary settings folder.

Avalonia headless renders the native chrome; it cannot render NativeWebView. The tests therefore also export the production reader HTML and its exact viewport bounds for a separate browser render. Combined comparison previews are assembled renders, not live desktop screenshots. Live desktop verification remains necessary for WebView focus, selection, native popup positioning, and Windows display scaling.
