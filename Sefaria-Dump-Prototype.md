# Sefaria dump prototype

This prototype tests whether Stndr can replace per-book downloads with a one-time
import of Sefaria's `dump_small.tar.gz` MongoDB dump. It reads BSON directly; it
does not require MongoDB or modify the source dump.

## Scope

The prototype imports:

- All 6,595 work/index records and all terms, because they are small and are
  needed to derive link titles and categories.
- A representative set of 30 works, selecting the best non-empty Hebrew and
  English version of each work.
- Every non-empty segment from those selected versions.
- Every link whose expanded endpoints contain one of 22 representative passage
  references.
- The version licence and source fields needed by a future Reader Tools licence
  expander.

The 30 works deliberately cover Tanakh prose and poetry, Mishnah, Bavli,
Yerushalmi, Midrash, Mishneh Torah, Shulchan Arukh, responsa, simple and complex
commentaries, Kabbalah, Chasidut, Musar, liturgy, and medieval Jewish thought.
The selection and its rationale are in `scripts/sefaria_dump_samples.json`.

## Run it

From the repository root:

```powershell
python scripts\sefaria_dump_prototype.py build `
  --dump-dir "F:\Git Repos\Hebrew Dictionary Concordance\data\raw\sefaria_mongo\dump\sefaria" `
  --database artifacts\sefaria-prototype.sqlite `
  --report artifacts\sefaria-prototype-report.json `
  --force
```

Re-run the integrity checks without rebuilding:

```powershell
python scripts\sefaria_dump_prototype.py verify
```

Inspect the non-commentary items which match Stndr's Links expander contract:

```powershell
python scripts\sefaria_dump_prototype.py query-links "Genesis 1:1"
```

Pass `--include-commentary` to include items which Stndr normally sends to its
separate Commentaries expander.

The prototype requires the `bson` package supplied by PyMongo. The generated
database and JSON report live under the ignored `artifacts/` folder.

### Full-corpus phase-one build

The optimized full-corpus experiment is separate from the deliberately
duplicative sample database:

```powershell
python scripts\sefaria_dump_full_import.py build `
  --dump-dir "F:\Git Repos\Hebrew Dictionary Concordance\data\raw\sefaria_mongo\dump\sefaria" `
  --database artifacts\sefaria-full.sqlite `
  --report artifacts\sefaria-full-report.json `
  --force
```

Re-run every integrity, text, Reader, and Links check with:

```powershell
python scripts\sefaria_dump_full_import.py verify `
  --database artifacts\sefaria-full.sqlite `
  --report artifacts\sefaria-full-verification.json
```

## Measured local result

The completed test imported 30/30 works, 60 versions, and 123,071 text segments.
Every stored text JSON hash and segment count matched the BSON source. Both
ordinary array texts and complex keyed-object texts were represented.

The link sample returned results for 21 of 22 references and imported 7,704
matching links. The one zero-result reference is retained as a useful negative
control. The SQLite database was approximately 311 MiB and the complete build,
including full scans of the 3.33 GiB text collection and 5.04 million links,
took 30-45 seconds across repeated runs on the test machine.

The optimized full build materializes 23,213,533 link endpoints. This includes
1,794 fallback endpoints for link sides where the dump omitted an explicit
`expandedRefs` array. A production
database should materialize and index these endpoints during import. It should
also derive the target work, display category, collective titles, and English
availability once during import, as this prototype does, rather than repeating
that work each time the Links expander opens.

## What maps to the Links expander

`links.bson` directly supplies:

- Both canonical endpoint references
- Expanded segment references for range links
- Link type
- Automatic/manual status and generator
- Anchor text where available
- Language availability for both endpoints
- Inline-citation metadata where available

Stndr's current API model also expects a target index title, display category,
and collective English/Hebrew titles. Those are derived by joining the link's
target reference to `index.bson` and `term.bson`. A Hebrew rendering of the full
target reference is not stored on the raw link and would require Stndr's future
reference formatter; the current UI already falls back correctly when it is
empty.

The raw data includes commentary links. Stndr can preserve its present division:
works whose index has `dependence: Commentary` go to the Commentaries expander,
while the remaining categories appear in Links.

## Production implications

The prototype establishes data availability and fidelity, but is not intended
to be shipped as the importer. Production work should use a .NET BSON reader,
stream the compressed archive without extracting all 11.46 GiB, import an
explicit allow-list of collections, and perform the database swap atomically
after verification.

The Reader Tools licence expander can be backed directly by the stored version
fields: `license`, `versionSource`, `versionTitle`, `actualLanguage`, and optional
version notes. The setting proposed for hiding that expander should default to
showing it.

## Full-corpus phase-one result

The complete optimized database passed SQLite integrity checking and full text
verification. Results on the test machine were:

| Measurement | Result |
| --- | ---: |
| Installed SQLite database | 2.036 GiB |
| Total build plus verification | 376 seconds |
| Works | 6,597 |
| Versions | 11,641 |
| Non-empty text segments counted | 5,033,076 |
| Unique canonical reference strings | 3,136,699 |
| Logical links | 5,043,382 |
| Materialized link endpoints | 23,213,533 |
| Text verification failures | 0 |
| Representative Reader failures | 0 |

The work count is two greater than `index.bson` because the text collection has
two orphaned titles, `Teruma` and `On the Account of Creation`. The importer
creates minimal synthetic work records rather than discarding either text.

### Storage

| Phase | Allocated size |
| --- | ---: |
| Metadata and schemas | 0.024 GiB |
| All compressed version texts | 0.896 GiB |
| References, logical links, and expanded endpoints | 0.904 GiB |
| Final query indexes | 0.213 GiB |

Compact text JSON occupied 3,557,477,160 bytes before compression and
939,505,733 bytes afterward, a ratio of 26.4%. Each version is compressed
independently, so opening one work does not require decompressing a corpus-sized
blob.

The phase-one schema deliberately stores the original nested version content,
not a second copy of five million segment rows. Reader parsing can therefore be
migrated with minimal semantic change. A future search index will add storage
and should be measured separately.

### Performance

All 11,641 compressed versions were decompressed, parsed, and hash-verified with
no failures. Opening the selected Hebrew and English versions for all 30 sample
works produced these warm-cache timings:

- Median complete fetch, decompression, and JSON parse: 2.8 ms
- Slowest selected version: 69.1 ms
- Selected Reader versions opened: 60/60

For the 22 representative Links references, indexed count queries and first-page
queries returned in 0.04-14.0 ms during the standalone verification run. The
largest case, `Genesis 1:1`, has 2,756 links but returns its first 100 without
materializing all controls.

Only five of more than ten million logical link sides failed to resolve to a
current work record. Four use the stale spelling `Shulchan Aruch HaRav` where
the index uses `Shulchan Arukh HaRav`; one uses `Pardes Rimonim` where the index
uses `Pardes Rimmonim`. Production should import schema aliases and a tiny
legacy override map. The links themselves and their endpoint queries remain
intact even when this optional work metadata join is unresolved.

## Phase-two Stndr implementation

Phase two replaces the extracted-directory prototype with a production C# path
inside Stndr. The setup dialog can either resume a download of Sefaria's
official `dump_small.tar.gz` or use an archive the user already has. It streams
`tar.gz -> tar entry -> BSON document -> SQLite`; the 11.46 GiB expanded dump is
never written to disk.

The importer has an explicit collection allow-list (`category`, `index`,
`links`, `person`, `term`, and `texts`). Operational and user-derived Mongo
collections are skipped. It retains every version and language, version licence
and source fields, work schemas and alternate structures, logical links, and
expanded link endpoints. Text JSON is independently zlib-compressed and
SHA-256 tagged.

Installation is transactional at the file level:

1. Build `database/sefaria-library-importing.sqlite`.
2. Run SQLite integrity and table-count checks.
3. Replace the active database atomically, retaining the prior database as
   `sefaria-library-previous.sqlite` during updates.
4. Write `database/sefaria-library.json` with source archive hash, time, schema
   version, and corpus counts.

Interrupted HTTP downloads remain as `.part` files and resume with a byte range
request guarded by ETag or Last-Modified. The complete-library prompt appears
after the Data folder is chosen, can be deferred, and remains available from
Settings. Existing per-book downloads remain untouched until Reader migration.

### Production importer verification

The C# importer was run twice against the complete local 2,448,315,930-byte
archive. The corrected full run completed in 232 seconds and produced a
2,394,611,712-byte database. Results exactly matched the phase-one corpus
counts:

| Verification | Result |
| --- | ---: |
| SQLite integrity check | `ok` |
| Works | 6,597 |
| Versions | 11,641 |
| Non-empty string segments | 5,033,076 |
| Logical links | 5,043,382 |
| Materialized link endpoints | 23,213,533 |
| Unique references | 3,136,699 |
| Compressed versions decompressed and JSON-parsed | 11,641/11,641 |
| Stored text hashes/length failures | 0 |
| Links associated with at least one work | 5,043,382/5,043,382 |
| Unresolved individual link sides | 5 (the known stale title spellings) |

The solution builds without warnings from the new code or dependencies. Two
unrelated warnings already present in the Reader/window code remain.

## Phase-three runtime integration

When `database/sefaria-library.sqlite` is installed, Stndr now treats its 11,641
versions as read-only installed books. Existing downloaded JSON remains a
fallback and wins if it has the same title/language/version key. Snapshot
versions are addressed internally by their SQLite version ID; the Reader only
decompresses the selected version blob.

Runtime integration covers:

- The Installed Library tree and the Library Manager tree
- Version selection, Reader text units, and navigation pages
- Work descriptions, schemas, and alternate structures
- The Links expander through indexed expanded endpoints
- The Commentaries expander, including locally resolved text excerpts where a
  matching version and segment exist
- Link previews through the existing installed-text preview path
- A bottom-of-Reader-Tools Licenses expander showing the selected versions'
  license and source

The Licenses expander is visible by default and can be hidden in Settings. Its
expanded/collapsed state is saved with each Reader tab. The Library Manager
labels snapshot versions as `Included` and disables individual download/delete
actions because the snapshot is updated as one atomic unit.

Default version selection now respects Sefaria's `isPrimary`, `isSource`,
priority, and non-empty segment metadata. This avoids selecting empty or minor
historical versions merely because their titles sort first alphabetically.

### Runtime verification

The Stndr service itself (not a standalone SQL script) was exercised against
the complete phase-one database:

| Runtime case | Result |
| --- | ---: |
| Virtual installed versions | 11,641 |
| Distinct text-bearing works | 6,466 |
| Library Manager tree books | 6,466 |
| Genesis Reader units | 1,533 |
| Berakhot Reader units / navigation pages | 2,749 / 127 |
| Mishnah Berakhot Reader units | 57 |
| Shulchan Arukh, Orach Chayim units / pages | 4,171 / 697 |
| Genesis 1:1 non-commentary links | 1,145 |
| Genesis 1:1 commentary links | 1,611 |
| Commentary items with locally matched Hebrew text | 394 |
| Genesis/Berakhot schema conversion | passed |
| Offline link preview | passed |

The 1,145 Links plus 1,611 Commentaries equal the previously measured 2,756
links for `Genesis 1:1`, confirming that the runtime category split preserves
the complete endpoint result. Some commentary rows have no local excerpt
because that precise linked segment is absent from the selected version; the
link metadata remains available.

Full-corpus text search is deliberately not migrated in this phase. Scanning
11,641 compressed versions sequentially would be the wrong production design;
it should be backed by the separately measured search index in a subsequent
phase. Offline lexicon tables are covered by the dictionary phase below.

## Complete-library update channel

Stndr retains the Library Manager as the update centre for the complete offline
library. Application updates and Sefaria data updates are separate channels:
application releases keep the existing blue banner, while available Sefaria
snapshots use a green library banner with a `Review` action.

Automatic library checking is enabled by default but performs only a metadata
request. It compares the official snapshot's ETag, Last-Modified value, and
content length with the remote source values stored in
`database/sefaria-library.json`. Downloads always require the user's explicit
approval. The check can be disabled or run manually in Settings, and the
Library Manager provides `Check for updates` / `Update library` actions.

The first update provider uses the complete `dump_small.tar.gz`. Updating
reuses the resumable Phase 2 downloader and verified staging importer, then
atomically swaps the database and retains the prior snapshot. Dismissing the
banner hides the notification without forgetting the available update.

The live metadata check on 22 July 2026 returned:

- ETag: `"5cbc6ec892a4fd4c68c587a11dab8f14"`
- Content length: 2,454,211,197 bytes
- Last modified: 22 July 2026 01:04:57 UTC

This was slightly newer and larger than the local 2,448,315,930-byte archive,
demonstrating a real update rather than a synthetic UI condition.

### Schema version 2 and future incremental updates

Schema version 2 retains Mongo `_id` values as `upstream_id` on works,
versions, and links. A future Sefaria incremental API can therefore be added as
another update provider which applies explicit upserts and deletions to a
staging copy before the same validation and atomic activation steps.

The schema-v2 C# importer was run over the complete local corpus:

| Verification | Result |
| --- | ---: |
| Import time | 288 seconds |
| SQLite integrity | `ok` |
| Works | 6,597 |
| Works with upstream IDs | 6,595 |
| Versions with unique upstream IDs | 11,641 / 11,641 |
| Links with unique upstream IDs | 5,043,382 / 5,043,382 |
| Materialized link endpoints | 23,213,533 |
| Non-empty string segments | 5,033,076 |
| Schema-v2 Reader/Links runtime test | passed |

The two works without IDs are the intentionally synthesized orphan text titles
already described in phase one.

## Offline dictionaries (schema version 3)

The streaming importer now also consumes `lexicon`, `lexicon_entry`, and
`word_form` directly from the archive. It keeps structured lexicon metadata,
entry content, alternate headwords, morphology and common identifiers (Strong,
GK, TWOT, root), plus contextual word-form references. Normalized Hebrew keys
support niqqud-insensitive and final-letter-insensitive lookup; SQLite FTS5
supports definition, transliteration, and identifier searches.

The Dictionary utility tab provides:

- a catalogue of every imported lexicon;
- lazy initial-letter and prefix drill-down, limited to 60 visible headwords;
- exact lookup, broad entry search, and optional per-dictionary search scope;
- previous/next headword navigation and the existing clickable citations;
- offline-first right-click lookup, with the Sefaria Words API retained as a
  fallback when no local result exists.

A focused import of the current dump produced 10 lexicons, 108,250 non-empty
entries, 332,123 non-empty word forms, and 600,605 form-to-entry relationships.
The dictionary-only SQLite data occupied about 440 MiB. The two entry records
and 597 word-form records omitted from the raw BSON counts have blank required
display keys. Integrity, catalogue, prefix browse, FTS definition search,
contextual Hebrew lookup, and reference recovery were tested against that
database.
