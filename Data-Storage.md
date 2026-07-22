# Data storage

This document records the rules for accessing Stndr's local application data.
These rules are part of the storage design and should be preserved when adding
features or changing persistence code.

## Installed-books database

The installed-books manifest is stored at:

```text
<data folder>/database/installed-books.json
```

`InstalledBooksDatabase` is the sole file-access gateway for this manifest.
Application code must not read or write `installed-books.json` directly.

The database class serializes reads and writes through one access queue. This
prevents two operations in the same `SefariaLibraryService` instance from opening
the file at the same time. A caller waiting to access the manifest must use the
queue rather than retrying or bypassing the database class.

`SefariaLibraryService` owns the database instance and recreates it whenever the
configured data folder changes. The service may cache installed-book records,
but cache refreshes and persisted changes must still go through
`InstalledBooksDatabase`.

All installed-book mutations currently converge on `SaveInstalledBooks`, which
delegates persistence to the database class. New mutation paths must do the same
or use a future transaction-style API provided by that class.

## Why access is serialized

On 22 July 2026, Stndr raised an unhandled `IOException` while saving a reader
position. A second operation already had `installed-books.json` open, so
`File.WriteAllText` failed on the UI thread and the application closed. The
queued database class was introduced to prevent this internal file-access race.

The queue coordinates Stndr's own operations. It cannot prevent an unrelated
external program from locking the file, so file-access errors must still be
allowed to surface with useful context rather than being silently discarded.

## Checklist for storage changes

When changing installed-book persistence:

1. Keep all manifest file access inside `InstalledBooksDatabase`.
2. Serialize reads as well as writes; a reader can collide with a writer.
3. Do not introduce a second queue or database instance for the same configured
   manifest within one service instance.
4. Keep the in-memory cache synchronized only after a write succeeds.
5. Recreate the database gateway when the configured data folder changes.
6. Consider a transaction-style queued update before adding read-modify-write
   operations that can run concurrently, so a stale snapshot cannot overwrite a
   newer change.

Future mutable database files should use a similarly explicit gateway when they
can be accessed from both UI and background operations.
