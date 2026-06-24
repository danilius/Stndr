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

## Releases

Version lives in `release/VERSION`. Velopack packages are built with `scripts/Release.ps1`.

**Local Windows release (build only):**

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Release.ps1
```

**Bump, build, and upload to GitHub Releases:**

```powershell
# Authenticate once: gh auth login  (or set GITHUB_TOKEN)
powershell -ExecutionPolicy Bypass -File .\scripts\Release.ps1 -Bump patch -Upload -Tag
```

**Explicit version:**

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Release.ps1 -Version 1.2.0 -Upload -Tag -PushTag
```

**CI / GitHub Actions:** push a tag such as `v1.2.0`, or run the **Release** workflow manually from the Actions tab. Releases are Windows-only for now.

Installed apps check `https://github.com/danilius/Stndr` for updates via the in-app banner.

## Project Layout

- `Stndr/` - Avalonia application source.
- `Stndr/Assets/` - application icons and visual assets.
- `Stndr/Data/` - local settings, cached Sefaria index, downloaded texts, and commentary cache. This folder is generated locally and is not tracked by Git.

## Notes

- The app uses Sefaria APIs and locally cached Sefaria data.
- Build output, IDE metadata, downloaded texts, and local settings are intentionally ignored.
- This project is licensed under the GNU Affero General Public License v3.0 or later.
- This project is not affiliated with, endorsed by, or sponsored by Sefaria.
