# SupernoteBridge

A portable, offline Windows companion app for the **Supernote** e-ink tablet (tested on the A6X2 Nomad).
Connect the tablet over USB, run one `.exe`, and manage it from your browser: browse and transfer files,
preview handwritten notes, export to PDF/TXT, print, back up the whole device, verify and restore backups,
and search your recognized handwriting. No installer, no account, no cloud, no internet.

> **Project status: final version, no further development.**
> SupernoteBridge does everything it was set out to do, and this release is final. No new features or fixes
> are planned, and issues and pull requests may not get a response. You're welcome to fork it and build on
> it under the license terms.

## Why

Supernote tablets connect over USB as an MTP device (no drive letter), and the official tools lean on the
cloud. The goal was a local tool that keeps notes on your own machine: full, verifiable backups, a quick way to
read and export notes, and no device content left behind on disk except what you explicitly save.

## How it was built

This project was written entirely by **Claude Opus** (Anthropic) in Claude Code, guided by the
repository owner: the owner defined the need, set the requirements and the security policy, tested every
feature on a real device, and approved or rejected each design. The reasoning behind the design is kept in
[`docs/decisions.md`](docs/decisions.md).

## Features

1. **Device files:** browse, download, upload (drag and drop, safe replace), rename, delete.
2. **Previews:** `.note`, annotated PDFs (`.mark` overlay), plain PDFs and images, with minimap, zoom and page jump.
3. **Exports:** vector PDF (default), raster PDF, TXT, PDF with ink; bulk export of a folder to PDF.
4. **Print:** current page or whole document through the browser's print dialog.
5. **Backups:** full, independent copy of the device into `YYYYMMDD-HHMMSS_SUPERNOTE_BACKUP\` with a `manifest.json` (path, size, mtime, SHA-256).
6. **Offline mode:** open a backup and browse, preview, print and export it without the device (read-only).
7. **Verify:** re-hash a backup against its manifest, in the UI or headless (`--verify`).
8. **Restore:** dry-run plan first, then overwrite or skip changed files; every file is read back and SHA-256-checked.
9. **Search:** file names plus the device's recognized handwriting text; case- and accent-insensitive.
10. **CLI and REST API** for scripted and scheduled backups.

**Security policy:** device content never touches the disk except for explicit downloads, exports and backups.
Previews, uploads, restore hashing and the search index live in RAM only, and content endpoints send
`Cache-Control: no-store`. The web UI binds to `127.0.0.1` only.

## Requirements

1. Windows 10/11 x64.
2. To build: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
3. To run a release build: nothing; the exe is self-contained.

## Build and run

```
git clone <this repository>
cd supernote-bridge
dotnet build -c Release
dotnet test
dotnet run --project SupernoteBridge.Host -c Release
```

The browser opens `http://127.0.0.1:8099`.

### Portable single-file exe

```
dotnet publish SupernoteBridge.Host -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

This produces `publish\SupernoteBridge.exe` (about 200 MB, includes the .NET runtime and the PDF engine).
Release builds strip local source paths and ship no PDB files (see `Directory.Build.props`).

## Configuration

`settings.json` is read from the folder that holds the exe and created with defaults on first run.
See [`settings.example.json`](settings.example.json):

| Key | Meaning |
|---|---|
| `port` | UI port (default 8099) |
| `open` | open the browser on start |
| `backupRoot` | default backup parent folder; `null` = ask every time / `BACKUP` beside the exe |

Command-line switches override `settings.json`:
`--port <n> --open --no-open --backup <dir> --quit --backup-on-attach --open-backup <dir> --verify <dir>`.

Scheduled backups: [`scripts/sn-backup.ps1`](scripts/sn-backup.ps1) wraps `--backup` with logging and exit codes for Task Scheduler.

## Documentation

1. [User manual](docs/SupernoteBridge_manual.md): every feature, the CLI, the REST API, troubleshooting.
2. [Design decisions](docs/decisions.md).
3. [`.note` metadata schema notes](SupernoteSharp/A5X_Metadata_Schema.md).

## Project layout

| Project | Purpose |
|---|---|
| `SupernoteBridge.Core` | device transport (WPD/MTP), backup-folder transport, services: render, export, backup, verify, restore, search |
| `SupernoteBridge.Host` | ASP.NET Core minimal API plus the single-page UI (`wwwroot/index.html`), embedded in the exe |
| `SupernoteBridge.Tests` | xUnit tests (in-memory device, temp folders; no device needed) |
| `SupernoteBridge.Spike` | developer diagnostics against a connected device |
| `SupernoteSharp` | vendored copy of [nelinory/SupernoteSharp](https://github.com/nelinory/SupernoteSharp), patched to parse from any `Stream` |

## Known limitations

1. Highlight-digest `.mark` files store strokes as vectors only and preview as blank pages.
2. MTP cannot set file dates, so restored files show the restore time on the device.
3. Only pages with device-recognized text are content-searchable; PDF/EPUB text is not indexed.
4. The app-private folder `Android\data` on the SD card cannot be read over MTP.

## License

The SupernoteBridge code is released under the [MIT License](LICENSE).
Third-party components keep their own licenses; **release binaries include GPL and LGPL components**,
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Supernote is a trademark of Ratta. This project is not affiliated with or endorsed by Ratta.
