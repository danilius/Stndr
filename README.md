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

## Notes

- The app uses Sefaria APIs and locally cached Sefaria data.
- Build output, IDE metadata, downloaded texts, and local settings are intentionally ignored.
- This project is licensed under the GNU Affero General Public License v3.0 or later.
- This project is not affiliated with, endorsed by, or sponsored by Sefaria.
