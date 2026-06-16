# Stendr

Stendr is a desktop reader for Jewish texts, built with Avalonia and .NET. It downloads and caches Sefaria library data locally so texts can be browsed and read from the app.

## Requirements

- [.NET SDK 10.0](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux supported by Avalonia

## Getting Started

Restore and run the app from the repository root:

```powershell
dotnet restore .\Stendr.sln
dotnet run --project .\Stendr\Stendr.csproj
```

Build a debug version:

```powershell
dotnet build .\Stendr.sln
```

## Project Layout

- `Stendr/` - Avalonia application source.
- `Stendr/Assets/` - application icons and visual assets.
- `Stendr/Data/` - local settings, cached Sefaria index, downloaded texts, and commentary cache. This folder is generated locally and is not tracked by Git.

## Notes

- The app uses Sefaria APIs and locally cached Sefaria data.
- Build output, IDE metadata, downloaded texts, and local settings are intentionally ignored.
- No open-source license has been selected yet.
