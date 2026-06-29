# Search

Stndr has an **Advanced Search** workspace for building reusable searches across installed books and, when online, the wider Sefaria library. Open it from the **Advanced Search** button in the main window.

Advanced Search is designed for study workflows where a plain text search is not quite enough: finding two terms near one another, locating letter patterns inside words, checking whether two ideas appear in the same segment or chapter, or running a Sefaria search without leaving Stndr.

## Before You Search

Most advanced searches run against your installed local library. If a book has not been downloaded, local searches will not include it. Use the Library Manager to download the texts and versions you want to search.

Each search has a **scope**. Leave the scope empty to search all local books, or choose one or more categories or individual works to narrow the results. Narrow scopes are usually faster and easier to review.

Search results are limited to the first 250 matches. If you get too many results, narrow the scope or use a more specific search.

Search matching ignores Hebrew vowel marks and normalizes final Hebrew letter forms. Punctuation and most symbols are ignored. Local searches work best when you search in the language of the downloaded text version being searched.

## Match Modes

Local searches use these match modes:

| Mode | Use it when | What it does |
| --- | --- | --- |
| Exact | You want the typed letters or words as entered. | Searches for the normalized term, keeping spaces between words. |
| Loose | You want a more forgiving match. | Ignores spaces while matching. |
| Ignore spaces | You are searching a phrase or letter sequence that may cross spacing boundaries. | Also ignores spaces while matching. |

Sefaria searches use these match modes:

| Mode | Use it when | What it does |
| --- | --- | --- |
| Exact | You want Sefaria exact-text search. | Searches Sefaria's exact field and sorts by canonical order. |
| Lemmatized | You want broader Sefaria search results. | Uses Sefaria's lemmatized search behavior and relevance scoring. |
| Nearby | You want words close to each other in Sefaria search. | Uses Sefaria lemmatized search with a word-distance setting. |

## Available Advanced Searches

### Find word/letters in scope

Use this for a focused local search for one word, phrase, or letter sequence.

Template:

`Find [word/letters] in [scope] matching [Exact/Loose/Ignore spaces]`

Good for:

- Finding every occurrence of a name, root, phrase, or recurring expression in installed books.
- Searching a specific work, such as one tractate or one Tanakh book.
- Starting broad, then narrowing the scope once you see the result shape.

Example:

Search for `covenant` in a downloaded English Tanakh translation with **Exact** matching. This finds segments containing that word in the selected scope.

Example:

Search for a Hebrew root or letter sequence in downloaded Hebrew texts with **Ignore spaces** when you care about the sequence more than spacing.

### Find letters inside a word

Use this when the letters may appear as part of a larger word rather than as a standalone word or phrase.

Template:

`Find [letters] inside a word in [scope]`

Good for:

- Looking for a short root or letter pattern inside longer words.
- Finding names or forms that may have prefixes or suffixes.
- Exploring spelling patterns in Hebrew texts.

Example:

Search for a three-letter Hebrew root in a downloaded Hebrew scope. Results include words where that sequence appears inside a longer normalized word.

Example:

Search for `king` in an English translation scope to find words such as `king`, `kingdom`, and `kingship`.

### Find A within N words/letters of B

Use this for local proximity searches where two terms must appear near each other.

Template:

`Find [A] within [N] [words/letters] of [B] in [scope] matching [Exact/Loose]`

Good for:

- Finding places where two themes appear close together.
- Searching for a phrase when wording may vary.
- Comparing word-distance and letter-distance results.

The distance can be measured in **words** or **letters**. Word distance counts word positions after normalization. Letter distance compares compacted text with spaces removed.

Example:

Search for `king` within `5` words of `priest` in downloaded Tanakh translations to find passages where the two roles appear close together.

Example:

Search for one Hebrew word within `10` words of another in a specific downloaded book to avoid scanning the whole library.

### Find A and B in the same segment/chapter

Use this when proximity is too strict, but both terms should still occur in the same textual unit.

Template:

`Find [A] and [B] in the same [segment/chapter] in [scope] matching [Exact/Loose/Ignore spaces]`

Good for:

- Finding conceptual pairings across a verse, mishnah, paragraph, page segment, or chapter.
- Looking for chapters where two recurring terms both appear.
- Reviewing broader contexts than a proximity search would return.

Choose **segment** for tighter results. Choose **chapter** when the relationship may be spread across a larger unit.

Example:

Search for `mercy` and `justice` in the same **chapter** of a downloaded English Tanakh translation.

Example:

Search for two Hebrew terms in the same **segment** of a downloaded commentary to find direct juxtapositions.

### Find A near B, excluding C

Use this when a proximity search returns too much noise and you want to remove results containing a third term.

Template:

`Find [A] within [N] [words/letters] of [B], excluding [C] in [scope] matching [Exact/Loose/Ignore spaces]`

Good for:

- Refining a proximity search by excluding a common false positive.
- Separating similar topics that share vocabulary.
- Searching for one pairing only when a third idea is absent from the same segment.

The exclusion is checked against the matched segment. If the excluded term appears in that segment, the result is removed.

Example:

Search for `king` within `8` words of `law`, excluding `war`, in downloaded English texts to focus on legal contexts rather than battle narratives.

Example:

Search for two Hebrew terms near each other, excluding a third Hebrew term that marks an unrelated formula.

### Search Sefaria for query in corpus

Use this when you want Sefaria-powered search from inside Stndr, including texts that may not be installed locally.

Template:

`Search Sefaria for [query] in [corpus] matching [Exact/Lemmatized/Nearby]`

Good for:

- Searching beyond your installed local library.
- Using Sefaria's lemmatized search.
- Opening remote matches and deciding whether to download the work.

Sefaria search requires an internet connection. Scope selection filters by Sefaria categories. Results can show whether the matched work is already downloaded; if it is not installed, Stndr can preview or open the remote result rather than jumping into a local reader tab.

Example:

Search Sefaria for `sabbath` in the **Tanakh** corpus with **Lemmatized** matching to get broader ranked results.

Example:

Search Sefaria for `king priest` with **Nearby** matching and a distance of `10` words to find places where the ideas occur close together.

## Saving Searches

After running a search, use **Save Search** to keep the query and its results. Enable **Autosave** if you want completed searches saved automatically.

Saved searches appear in the **Saved Searches** panel. You can reopen them, rerun them, rename them, pin important searches, or delete searches you no longer need.

Use saved searches for repeatable research questions, such as "all appearances of a term in a downloaded corpus" or "a proximity search I want to revisit after downloading more books."

## Practical Workflow

1. Start with a narrow scope, such as one work or category.
2. Use **Find word/letters** to confirm the basic term appears.
3. Switch to **Find A within N words of B** if you are testing a relationship between two terms.
4. Use **same segment/chapter** when the relationship is broader than a short distance.
5. Add an exclusion term when repeated false positives share a common word.
6. Save the search once the results are useful enough to revisit.

For best results, download the texts you expect to search locally, choose the smallest useful scope, and treat Sefaria search as the online companion for broader discovery.
