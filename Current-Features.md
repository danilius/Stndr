# Current Features

Stndr is a desktop reader for Jewish texts. It lets you browse the Sefaria library, download texts and translations, and read them in a local, customizable workspace.

Stndr is designed around cached reading. Downloaded texts are stored in your Stndr Data folder and can be opened again without re-downloading them. Some connected features, such as first-time downloads, update checks, Sefaria metadata, commentary lookups, and linked-text previews, may still require an internet connection.

The current release is **v0.0.3-alpha**.

## Library

The Library Manager is where you choose which texts Stndr keeps locally.

- Browse the Sefaria library by category and book.
- View book and category details before downloading.
- Download Hebrew texts and translations for individual books.
- Delete downloaded texts when you no longer need them locally.
- Download or delete supported categories in bulk.
- Choose specific Hebrew and translation versions when multiple versions are available.
- Keep all downloaded content in a configurable Stndr Data folder.

## Reader

Downloaded books open in reader tabs, so you can move between multiple texts during a learning session.

- Open installed texts from the Installed Books panel.
- Use multiple tabs and close or reorder them as needed.
- Restore open reader tabs, selected texts, layout, and reading position between sessions.
- Read Hebrew text, translation, or both together.
- Switch between downloaded Hebrew versions and translations for the current work.
- Copy selected text from the reader.
- Collapse or resize the side panels to focus on the text.

## Navigation

Stndr provides navigation tools that adapt to the structure of the selected work.

- Jump between chapters, pages, sections, or other available navigation markers.
- Use schema-aware navigation for Tanach and Bavli.
- Use topic-based navigation for works such as Shulchan Aruch.
- Expand and collapse navigation groups.
- Use the jump box to move quickly within available navigation markers.
- For Chumash, browse sedrot and aliyot and jump directly to an aliyah.

## Display Options

Reader display can be adjusted for different study styles and screen sizes.

- Switch between Hebrew-only, translation-only, and dual-language layouts.
- Choose how Hebrew marks are displayed where supported.
- Adjust reader column widths for single-language and dual-language layouts.
- Set UI font and size.
- Set separate Hebrew and English reader fonts and sizes.
- Set separate Hebrew and English commentary font sizes.
- Choose whether installed book titles appear in Hebrew, English, or both.

## Commentaries

When you select a paragraph or segment, Stndr can load related commentaries from Sefaria.

- View commentaries for the selected passage.
- Open commentaries in the Reader Tools panel or in a split view beside the text.
- Toggle commentary language between English and Hebrew.
- Sort commentary sources.
- Pin preferred commentary sources.
- Reorder commentary source groups.
- Preserve commentary display preferences between sessions.

## Links And Source Previews

Stndr can show Sefaria links connected to the selected passage.

- Load linked sources for the current selection.
- Filter links by category.
- Preview linked text before opening it.
- Open linked works in a new tab.
- Show linked text in split view beside the current reader.
- Download a linked work when it is not already installed.
- Use cached linked-text previews where available.

## Advanced Search

The Advanced Search workspace supports reusable searches across installed local texts and, when online, the wider Sefaria library.

- Search installed texts by word, phrase, or letter sequence.
- Search for letters inside larger words.
- Find one term within a chosen word or letter distance of another term.
- Find two terms in the same segment or chapter.
- Exclude a third term from proximity results.
- Limit searches to selected books or categories.
- Search Sefaria online from inside Stndr.
- Save searches, reopen them, rerun them, rename them, pin them, or delete them.
- Open search results in the reader when the text is installed.
- Preview or open remote Sefaria results when the text is not installed.

## Local Data And Caching

Stndr stores its working data in a user-selected Stndr Data folder.

- Downloaded Hebrew sources and translations are stored locally.
- The Sefaria index and metadata are cached locally.
- App settings are stored with the selected data folder.
- A small pointer file outside the data folder remembers where the selected data folder is.
- The data folder can be changed later from Settings.

## Updates

On supported release builds, Stndr can check for app updates and show them without interrupting reading.

- See update availability in an in-app banner.
- Download an update from the banner.
- Restart Stndr when the update is ready.
- Dismiss the banner and continue using the app.

## Known Limitations

Stndr is still an alpha application, so some areas are intentionally limited or still changing.

- Release packages are currently Windows-focused.
- First-time library setup, text downloads, update checks, and some Sefaria-powered lookups require an internet connection.
- Cached reading works best for texts and translations that have already been downloaded.
- Commentaries, links, and previews depend on Sefaria data and may be incomplete for some passages.
- Bulk download behavior is tuned for selected large categories and may not be equally polished for every category.
- The app is not affiliated with, endorsed by, or sponsored by Sefaria.
