# Stndr

Stndr is a desktop reader for Jewish texts, built with Avalonia and .NET. It downloads and caches Sefaria library data locally so texts can be browsed and read from the app.

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux supported by Avalonia

## Getting Started

Restore and run the app from the repository root:

```powershell
dotnet restore .\Stndr.sln
dotnet run --project .\Stndr\Stndr.csproj
```

Build a debug version:

```powershell
dotnet build .\Stndr.sln
```

## Project Layout

- `Stndr/` - Avalonia application source.
- `Stndr/Assets/` - application icons and visual assets.
- `Stndr/Data/` - local settings, cached Sefaria index, downloaded texts, and commentary cache. This folder is generated locally and is not tracked by Git.

## Sefaria metadata LiteDB ingestion (POC)

This repository now includes a metadata-ingestion path that reads selected collections from a MongoDB dump archive (`*.tar.gz`) and writes a query-oriented LiteDB file.

Run from repository root:

```powershell
dotnet run --project .\Stndr\Stndr.csproj -- --ingest-sefaria-metadata --dump "F:\Git Repos\Stendr\sefaria dump_small.tar.gz" --out "F:\Git Repos\Stendr\Stndr\Data\Sefaria\metadata.db"
```

Optional flag:

- `--batch-size <int>` (default: `5000`)

Collections populated:

- `works` (from `index.bson`)
- `versions` (from `texts.bson`, metadata only)
- `categories` (from `category.bson`)
- `terms` (from `term.bson`)
- `title_lookup` (cross-entity normalized title lookup)
- `link_edges` (bidirectional edges from `links.bson`)
- `topic_links` (from `topic_links.bson`)
- `ingestion_info` (ingestion summary and source dump info)

Deterministic keying contract for source text JSON linkage:

- `text_version_key = <workKey>|<languageCode>|<versionTitle>`
- `json_file_name = <safeWorkKey>__<safeLanguage_versionTitle>.json` (or `__default.json` when version title is blank)
- file-name sanitization uses the same invalid-character stripping helper as existing downloaded-text file paths.

## Notes

- The app uses Sefaria APIs and locally cached Sefaria data.
- Build output, IDE metadata, downloaded texts, and local settings are intentionally ignored.
- This project is licensed under the GNU Affero General Public License v3.0 or later.
- This project is not affiliated with, endorsed by, or sponsored by Sefaria.
