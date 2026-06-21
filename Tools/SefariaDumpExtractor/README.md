# Sefaria Dump Extractor

Production-oriented CLI for exporting Sefaria `texts.bson` documents from a `mongodump` archive (`.tar.gz`) into deterministic JSON files.

## Strategy

This extractor uses **direct BSON parsing from the archive**:

1. Stream `index.bson` to build title/category metadata lookup.
2. Stream `texts.bson` and export one JSON file per source document.

This avoids operational dependency on `mongod` + `mongorestore` for this pipeline while retaining high throughput and strong resumability.

## Command

```powershell
dotnet run --project .\Tools\SefariaDumpExtractor\SefariaDumpExtractor.csproj -- `
  --dump "F:\Git Repos\Stendr\sefaria dump_small.tar.gz" `
  --output ".\artifacts\sefaria-export"
```

Optional flags:

- `--checkpoint <path>`: custom checkpoint file path (default: `<output>\checkpoint.json`)
- `--checkpoint-every <n>`: checkpoint write cadence by processed sequence (default: `1000`)
- `--overwrite`: overwrite existing export files instead of skipping
- `--max-docs <n>`: stop after exporting `n` docs (smoke runs / profiling)

## Output contract

Each exported file is a JSON object:

- `schemaVersion`: currently `1`
- `exportedAtUtc`: timestamp
- `sequence`: monotonically increasing sequence from `texts.bson`
- `documentId`: source Mongo `_id` as string
- `title`, `hebrewTitle`, `language`, `versionTitle`, `reference`
- `primaryCategory`, `categories`
- `source`: full original BSON document serialized as relaxed extended JSON

The path is deterministic and stable:

`texts/<primaryCategory>/<title>/<language>/<versionTitle>/<reference>__<stableHash>.json`

Where each segment is slugified and the suffix hash is derived from `documentId|reference`.

## Resumability and idempotency

- Checkpoint stores `lastSequenceProcessed` per dump path.
- Resume skips already-processed sequence values.
- Existing files are skipped by default (idempotent reruns).
- `--overwrite` forces deterministic rewrites.

Metadata for downstream indexing is appended to:

`<output>\export-metadata.ndjson`

Each line includes run id, identifiers, categories, and exported relative path.

## Performance notes

Dominant costs at scale:

1. Archive decompression (`gzip`) and BSON decode.
2. JSON serialization of full source document.
3. Disk writes (many small files).

Expected scaling behavior is near-linear with document count. Throughput is highest when output is on SSD and checkpoint cadence is moderate (e.g., 1000-5000).

## LiteDB integration points

Two stable integration surfaces are already available:

1. `export-metadata.ndjson` as append-only ingest stream.
2. Deterministic file path contract keyed by title/category/language/version/reference.

To add direct LiteDB writes, introduce a second metadata sink implementation alongside `NdjsonMetadataSink` and wire it in the main extraction loop where `WriteAsync(exportRecord)` is called.
