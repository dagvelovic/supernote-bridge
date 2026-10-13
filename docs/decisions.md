# Decisions — SupernoteBridge

Architectural decision log kept during development. Each entry: context, decision, and the alternatives that were rejected.
"The owner" is the project owner who set requirements and approved designs; the code was written by Claude (Anthropic) under that guidance.

## [2026-08-20] Stack: C# .NET 8 single-file exe hosting a local web UI (not pure web app)
Context: Supernote A6X2 exposes itself over USB as MTP (Android 11 base), not USB mass storage. Browsers cannot talk MTP (WebUSB cannot claim an interface already bound to the Windows MTP driver). A pure browser app therefore cannot meet the USB requirement.
Decision: .NET 8 self-contained single-file `SupernoteBridge.exe` (win-x64) running Kestrel on `http://127.0.0.1:8099`, serving an embedded SPA. Opens default browser on start. No installer, no registry, no admin, runs from any folder.
Alternatives rejected: (1) Pure PWA — no MTP access. (2) WPF/WinUI desktop — works, but web UI reuses the same frontend for the WiFi path and is easier to iterate; WinUI also needs MSIX/runtime bits that hurt portability. (3) Python (supernote-tool) + Electron — not portable, heavy.

## [2026-08-20] Device transport: MTP via Windows Portable Devices (WPD), WiFi "Browse & Access" as second transport
Context: USB = MTP; device also serves an HTTP file server over WiFi (Browse & Access, port 8089) used by snbackup.
Decision: `IDeviceTransport` abstraction with two implementations: `WpdTransport` (MediaDevices NuGet, MIT, wraps PortableDeviceApi COM) and `BrowseAccessTransport` (HttpClient). USB is primary.
Rejected: libmtp/libusb + WinUSB driver swap — requires replacing the Windows driver (Zadig); breaks Explorer access, not portable.

## [2026-08-20] .note rendering: SupernoteSharp (C#) with its PDF/PNG/TXT converters
Context: Reference implementation is jya-dev/supernote-tool (Python, Apache-2.0); SupernoteSharp is its C# port (supports A6X2 Chauvet 3.x RLE format, vector PDF via VectSharp, realtime-recognition text export).
Decision: Depend on SupernoteSharp; vendor as source if NuGet lags firmware. Keep a test corpus of .note files from the actual device per firmware version.
Rejected: Re-implementing the RATTA_RLE decoder from scratch — only if SupernoteSharp fails on a new firmware.

## [2026-08-20] To-Do access: cloud-protocol emulation, not USB
Context: To-Do data is app-private on the device and not present in the MTP-exposed storage; tasks only leave the device through the Supernote Cloud / Private Cloud sync protocol (community OpenAPI spec, implemented by allenporter/supernote). No ADB, no local REST API on the device.
Decision: Phase 4 = spike first (confirm nothing task-related is reachable via MTP), then implement the minimal subset of the Private Cloud protocol needed for task sync inside the same exe; device is pointed at `http://<pc-ip>:8099` as its Private Cloud. To-Do read/write is therefore WiFi-only and is the highest-risk feature.
Rejected: Rooting / ADB — device does not expose ADB; voids supportability.
Superseded: see the next entry (To-Do dropped).

## [2026-08-20] Scope & layout answers from the owner
Decision:
1. Backup root defaults to a `BACKUP\` folder beside the exe; excluded from git.
2. **To-Do feature dropped entirely.** Phase 4 and spikes S0.3/S0.4 removed; no Private Cloud emulation. App scope = backup, file browse/transfer, note view/export.
3. PDF export defaults to **vector** (searchable strokes via VectSharp); raster is the opt-in flag.
Rationale: keeps everything portable and self-contained in one folder; To-Do was the highest-risk lowest-value feature.

## [2026-08-20] Security policy: device content never touches disk (RAM-only processing)
Context: owner requirement — any content copied from the device for preview/render/processing must leave zero trace on the computer; security is first priority.
Decision:
1. All transient device content lives in RAM only: .note preview downloads to MemoryStream, parses and renders in memory, serves PNG bytes from memory. Upload buffering also RAM (was %TEMP% file).
2. Preview/page/download endpoints send `Cache-Control: no-store` so the browser never writes device content to its own disk cache.
3. Only sanctioned disk writes: explicit user downloads (browser Save), exports, and the `BACKUP` mirror.
4. The planned on-disk PNG page cache is cancelled → RAM-only cache (last opened note).
5. Spike output (`spike-out\`) is dev-only and must be deleted after use.
Residual risk (accepted): the OS may page any process RAM to the pagefile; not preventable from user mode.

## [2026-08-20] SupernoteSharp vendored into the repo (replaces NuGet package)
Context: RAM-only policy requires parsing from MemoryStream, but the NuGet API is typed `FileStream`; the package also lags firmware signatures.
Decision: Vendored the library source (MIT) as `SupernoteSharp\` project in the solution; patched `FileStream fileStream` → `Stream fileStream` (11 signatures), TFM → net8.0-windows. LoadNotebook reads all content eagerly, so the stream can be disposed right after parse.
Rejected: temp-file bridge to keep NuGet — writes device content to disk, violates the security policy; deleted files are recoverable.

## [2026-08-20] TOTALPATH vector rendering aborted
Context: highlight-digest .mark files store strokes only as TOTALPATH vectors + undocumented highlight structures; bitmap layers blank. Rendering them needs a custom vector renderer (~0.5d) plus open-ended highlight reverse-engineering.
Decision: not worth it — such files preview as clean pages. Raw .mark files are still backed up byte-exact, so no data loss. Revisit only if upstream (supernote-tool/SupernoteSharp) implements it.

## [2026-08-20] Backups are ALWAYS full physical copies — hardlink increments removed
Context: the first backup implementation used NTFS hardlinks against the previous timestamped backup for unchanged files (fast, delta storage). Owner: time and disk volume are irrelevant; backup integrity is the priority — a single shared physical copy means one on-disk corruption touches every backup that links to it.
Decision: every run downloads every file fresh from the device into its own timestamped folder; no hardlinks, no reuse of prior backups. Each folder is a fully independent physical copy; manifest sha256 still enables integrity verification per backup.
Rejected: hardlink/--link-dest increments — single-copy corruption risk contradicts the stated priority.

## [2026-09-16] Printing is done in the browser from page PNGs, not via a server-side PDF
Context: print buttons (current page / whole document) were requested for note previews and PDFs, opening the printer-selection dialog.
Decision: pure client-side. A hidden iframe is filled with one `<img>` per page from the existing RAM-only page endpoints, then `contentWindow.print()` opens the browser's print dialog. Plain PDFs go through the same path (PdfPreviewService rasterizes them without a .mark), so all document types print identically with handwriting included.
Rationale: zero new server code or disk writes (security policy intact); the browser dialog already offers printer, paper and orientation; page images are exactly what the viewer shows. Alternatives rejected: (1) printing the `<embed>` PDF viewer — no cross-frame access to the plugin, and annotated PDFs would lose ink; (2) generating an export PDF and opening it for print — writes a download or needs a temp file, slower for a single page.

## [2026-10-09] Offline mode = read-only backup folder behind IDeviceTransport, global source switch
Context: open an existing backup from disk through an open dialog and browse/preview/print/export it without the device.
Decision: `BackupFolderTransport` implements `IDeviceTransport` over a backup folder (whose layout already mirrors device paths). A `SourceRouter` registered as `IDeviceTransport` routes to the device or to the loaded backup, so NoteRenderService, PdfPreviewService, ExportService and the UI's page endpoints work unchanged. Backup mode is read-only: backups are integrity copies and are never modified. BackupService stays bound to WpdTransport. Picking is done with a native OpenFileDialog on the backup's `manifest.json`, which makes "this is a backup" explicit.
Rationale: the smallest change that reuses all the existing viewer code; RAM-only rendering is unaffected (the source files are already sanctioned disk content).
Alternatives rejected: (1) a `?src=` parameter on every endpoint (device and backup side by side) — touches every route and the whole UI for little gain; (2) importing/copying the backup somewhere — pointless duplication; (3) FolderBrowserDialog — works, but a file open dialog was requested, and picking manifest.json rejects random folders.

## [2026-10-09] No fixed backup location — the user picks it every time
Context: with `backupRoot: null` the 💾 Backup and 📂 Open backup dialogs start in a non-existent `BACKUP` folder beside the exe, so Windows falls back to the last-used folder. Pinning `backupRoot` to a fixed folder was suggested.
Decision: rejected. `backupRoot` stays null by default and both dialogs ask every time.

## [2026-10-09] Verify / Restore / Search: design
1. Verify never writes into the backup; a report is only an explicit browser download. Rejected: writing verify-*.json into the backup folder (it would modify the backup).
2. Restore writes only through WpdTransport, to the file's original path, with a dry-run plan first, safe overwrite (temp name → verify → swap) and a read-back SHA-256 check. Rejected: restoring to an arbitrary folder (that's just upload; keep restore simple); keep-both copies of .note files (two notes with the same internal FILE_ID on the device, effect on links/sync unknown).
3. The search index is RAM-only, rebuilt each session (security policy: no device content on disk, and an index of text is device content). Rejected: an on-disk index cache. Diacritic-insensitive matching because the notes may be in Croatian or other languages with diacritics.
4. Restore conflict (device has a different version): the preview lists the conflicts with both sizes and dates; the user chooses **overwrite or skip** per restore. No "keep both".
5. Live restore tests write to the device **only** in `\Internal shared storage\Note\_sb_restore_test\`, which the test creates and deletes. No real note is touched.
