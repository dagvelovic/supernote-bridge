# SupernoteBridge — User Manual

Version: 2026-10-09 · Target device: Supernote A6X2 Nomad (USB/MTP) · Platform: Windows 10/11 x64

SupernoteBridge is a **portable** companion app for the Supernote e-ink tablet. With the device
connected over USB it can browse, upload, download, rename and delete files, preview handwritten
`.note` files and annotated PDFs page by page, export them to PDF/TXT, and create full timestamped
backups of the whole device. Without the device, it can open any of those backups from disk and
browse them the same way (offline mode, read-only).

No installer, no registry entries, no account, no internet access — everything runs locally.

---

## 1. Quick start

1. Connect the Supernote with its USB cable (screen unlocked; it appears in Windows Explorer as an MTP device — it has **no drive letter**, which is normal).
2. Run `SupernoteBridge.exe`.
3. Your default browser opens `http://127.0.0.1:8099` — the file browser is on the left, the preview pane on the right.
4. The green **connected** badge in the header confirms the device is found. Plugging/unplugging is detected automatically within ~3 seconds.

## 2. Portability

- The exe is fully self-contained: .NET runtime, web UI, and PDF engine are all inside the single file. It runs on any Windows 10/11 x64 machine **without .NET installed**.
- You can move/copy `SupernoteBridge.exe` (plus, optionally, `settings.json`) to any folder or USB stick and run it from there.
- `settings.json` is read from the folder the exe is in. If missing, it is recreated with defaults on first run.
- If `backupRoot` is not set, the default backup target is a `BACKUP` folder **next to the exe** (it travels with the app).
- First launch from a new location may show a one-time Windows SmartScreen warning (unsigned exe): *More info → Run anyway*.
- The app binds only to `127.0.0.1` — nothing is reachable from the network, and no firewall prompt should appear.
- Technical note: at startup, native libraries (PDFium, Skia) are extracted to the machine's `%TEMP%\.net\` folder. That is program code, not your data. On a read-only medium the app still runs; it just cannot save `settings.json` and uses defaults.

## 3. The web UI

### 3.1 File browser (left half)
- **Folders**: click to enter; breadcrumbs at the top jump back to any level.
- **Files**: click a file name to download it.
- **Upload**: drag files from Explorer onto the dashed *drop* zone — they upload into the current folder. A file that already exists is replaced safely: the new copy is uploaded under a temporary name first, and the old one is removed only after the new one is complete.
- **✏️ Rename**: renames a file or folder in place on the device.
- **🗑 Delete**: deletes a file, or a folder with everything in it (confirmation asked).
- **📤 (on folders)**: bulk-export every `.note` under that folder to PDF — see 3.4.

### 3.2 Previews (right half)
- **👁 on a `.note` file**: opens the handwriting viewer.
- **👁✍ on a `.pdf` that has a sibling `.pdf.mark` file**: opens the annotated viewer — your handwriting is overlaid on the PDF pages exactly as on the device.
- **👁 on a plain `.pdf`**: opens the browser's built-in PDF viewer.
- **👁 on images** (`.png`, `.jpg`): shows the image.

Viewer controls (paged previews):
| Control | Action |
|---|---|
| ‹ › buttons, ← → keys | previous / next page |
| page number box | type a page number + Enter to jump |
| − / + / fit | zoom out, zoom in, fit to window (also `-` / `+` keys) |
| minimap (right strip) | scrollable page thumbnails; click to jump; current page highlighted |
| ✕ or Esc | close the preview |

First open of a large file takes a few seconds (it is copied from the device into memory); paging afterwards is instant.

Printing (buttons in the preview header):
| Button | Where | Action |
|---|---|---|
| **🖨 page** | `.note` and annotated-PDF viewers | prints the page currently shown |
| **🖨 all** | `.note`, annotated and plain PDFs | prints every page of the document |
| **🖨** | image previews | prints the image |

Each page is rendered exactly as in the viewer (handwriting included) and placed on its own sheet with a 10 mm margin; the browser's print dialog then opens so you can pick the printer, paper size and orientation. While pages are being prepared the header shows `preparing n/N…`; closing the preview (✕ / Esc) cancels. Nothing is written to disk — pages go straight from memory into the browser's print pipeline.

### 3.3 Exports (single file)
Export links appear in the preview header:
- **⬇PDF** — vector PDF (default): strokes become real vector paths — crisp at any zoom, selectable/searchable. Slower to generate.
- **⬇PDF·raster** — raster PDF: each page is an image. Fast, exact visual copy.
- **⬇TXT** — text from the device's realtime handwriting recognition (empty note → placeholder line; pages without finished recognition are skipped).
- **⬇PDF+ink** (annotated PDFs) — produces a new PDF with your handwriting permanently merged into every page (150 dpi pages).

### 3.4 Bulk export
Click **📤** on any folder row:
1. A Windows folder dialog asks where to put the PDFs.
2. A prompt asks vector (OK) or raster (Cancel).
3. Every `.note` under the device folder is exported to PDF, preserving the folder structure. Progress and a cancel option appear in the header.

### 3.5 Backup
Click **💾 Backup**:
1. A Windows folder dialog asks for the target parent folder.
2. A new folder `YYYYMMDD-HHMMSS_SUPERNOTE_BACKUP` is created there.
3. The complete device tree (all storages, files **and empty folders**) is downloaded into it. Every backup is a **full, independent physical copy** — no links to or reuse of earlier backups.
4. Each backup contains a `manifest.json` listing every file with size, modification time, and SHA-256 hash, so any backup can be integrity-checked later.
5. Progress (files, MB, current file) shows in the header; **✖ Cancel** aborts.

Notes: the app-private folder `\SanDisk SD card\Android\data` cannot be read over USB (a warning is counted, this is normal). A full backup of ~1.5 GB takes about 2 minutes over USB.

### 3.6 Offline mode — browsing a backup
You can read any earlier backup **without the device**, using the same viewer.

1. Click **📂 Open backup** in the header.
2. A Windows *Open* dialog appears. Go into the `YYYYMMDD-HHMMSS_SUPERNOTE_BACKUP` folder you want and pick its **`manifest.json`**.
3. An amber badge `BACKUP <date> · <serial> · read-only` shows that the backup is open; the breadcrumb root reads **backup** instead of **device**.
4. Browse, preview (👁 / 👁✍), print, export (⬇) and bulk export (📤) work exactly as with the device.
5. Click **🔌 Device** to return to the live device.

Rules in offline mode:
- **Read-only**: rename, delete and upload are hidden and refused by the server. A backup is never modified; even a bulk export into the open backup folder is refused.
- Plugging the device in while a backup is open does **not** switch away from the backup. **💾 Backup** keeps working, and always backs up the device, never the open backup.
- Hover the badge for the backup's full path and file count.
- Previews still render in RAM only; the backup files are only read.

To start directly in offline mode use `--open-backup <folder>` (see section 4).

### 3.7 Verifying a backup
With a backup open, click **✔ Verify**. Every file listed in the backup's `manifest.json` is read and its SHA-256 checksum compared with the one recorded at backup time. Progress (files, GB, MB/s) shows in the header; **✖ Cancel verify** stops it.

The result appears above the file list:
- **✔ Backup intact**: every file matches and the folder holds nothing unexpected.
- **✖ n problem(s)**, one line per finding:

| Finding | Meaning |
|---|---|
| `MISSING` | listed in the manifest, but the file is gone |
| `SIZE` | the file is a different size than recorded |
| `HASH` | same size, different content (bit rot, edited, or corrupted copy) |
| `UNREADABLE` | the file could not be read (locked, permissions, disk error) |
| `EXTRA` | a file in the folder that the manifest doesn't list (e.g. an interrupted backup, or added later) |
| `TEMP` | a `*.sb-tmp` leftover of an interrupted backup |
| `MANIFEST` | `manifest.json` is missing or unreadable |

Verify only reads: nothing is written into the backup. **⬇ save report** downloads the result as a JSON file to wherever your browser saves downloads. A 1.5 GB backup takes from a few seconds (recently read, cached by Windows) to about half a minute.

For scheduled checks use `--verify <folder>` (section 4).

### 3.8 Restoring from a backup
Bring a file or a whole folder from a backup back onto the device, to the **same place** it was backed up from. Use it to recover a deleted note, or to roll a note back to an earlier version.

1. Connect the device and open a backup (**📂 Open backup**).
2. Every row in the backup gets a **⬆** button (shown only while the device is connected). Click it on a file or a folder.
3. The app first **checks** every file against the device; nothing is written yet. A summary appears:
   - **new**: missing on the device, will be restored
   - **identical**: the device already has exactly this file, skipped
   - **different**: the device has another version; a table shows both sizes and dates
   - **damaged**: the backup copy fails its checksum, skipped (run ✔ Verify)
   - **blocked**: a folder with that name is on the device, skipped
4. Confirm:
   - without differences: **⬆ Restore n file(s)**
   - with differences: **⬆ Restore, overwrite n different** (the device versions are replaced and lost unless a newer backup holds them), or **⬆ Restore, skip different** (only missing files come back)
   - **✖ Cancel** leaves the device untouched.
5. The result lists every file: *restored (verified)*, *skipped: …* or *failed: …*.

How it stays safe:
- Each file is first written under a temporary name (`….sb-restore`), read back from the device and compared with the backup's SHA-256, and only then put in place of the device file. A failed or corrupt transfer never costs you the device copy.
- Restoring a `.pdf` also restores its `.pdf.mark` (your handwriting on it).
- Missing folders are recreated, including empty ones.
- The backup is only read. A warning appears if the backup comes from a different device.
- Limitation: over USB the app cannot set file dates, so restored files show the time of the restore on the device. Their content is byte-identical (verified).
- Tip: restored notes may appear on the device after leaving and re-entering the folder.

### 3.9 Search
The search box above the file list searches the **current source** (the device, or the open backup):
- **file and folder names** everywhere on it
- **your handwriting**: the text the Supernote recognized on each `.note` page (the same text as ⬇TXT)

Type two or more characters. Matching ignores case and Croatian/other accents (`proracun` finds *Proračun*), and with several words every word must occur. Results show:
- matching **names**: click a folder to open it, or a file to jump to its folder (👁 previews it directly)
- matching **pages**: note name, page number and a snippet with the words highlighted; click to open the note **at that page**

Tick **this folder** to search only inside the folder shown. **← back to folder** or Esc returns to the file list.

The first search builds an index in the background: folders first (names are searchable at once), then the text of every note. The status next to the box shows progress, then e.g. `29 notes · 80 pages with text, 182 without (not searchable)`. Results refresh while indexing. From the device this takes a moment because every note is read over USB once per session; clicking into the search box later picks up changed files.

Limits:
- Only pages with recognized text can be found by content. On the Supernote, handwriting is recognized when recognition is on for that note (e.g. realtime recognition, or a note type that converts text). Titles and keywords are stored as images, not text.
- PDF and EPUB text is not searched.
- The index lives in memory only and is never written to disk; it's rebuilt after the app restarts or the source changes.

## 4. Command-line switches

```
SupernoteBridge.exe [--port n] [--open | --no-open] [--backup <dir>] [--backup-on-attach] [--quit] [--open-backup <folder>] [--verify <folder>] [--help]
```

| Switch | Meaning |
|---|---|
| `--port <n>` | HTTP port for the UI (default 8099, or `port` from settings.json) |
| `--open` | open the browser on start (default) |
| `--no-open` | do not open the browser |
| `--backup <dir>` | headless mode: wait for the device (up to 120 s), run one full backup into `<dir>`, print progress to the console |
| `--quit` | with `--backup`: exit when the backup finishes |
| `--backup-on-attach` | run a backup automatically every time the device is plugged in (target: `--backup` dir, else `backupRoot`, else `BACKUP` beside the exe) |
| `--open-backup <folder>` | start in offline mode with this backup folder (or its `manifest.json`) open |
| `--verify <folder>` | check a backup folder (or its `manifest.json`) against its manifest, print each problem, then exit; no web server is started |
| `--help` | show usage |

Exit codes (headless backup): `0` backup OK · `1` backup failed · `2` no device within 120 s.
Exit codes (`--verify`): `0` backup intact · `1` problems found (or cancelled/failed) · `2` not a backup (no `manifest.json`).

Examples:
```
SupernoteBridge.exe --port 9000 --no-open
SupernoteBridge.exe --backup "D:\SupernoteBackups" --quit --no-open
SupernoteBridge.exe --backup-on-attach --no-open
SupernoteBridge.exe --open-backup "D:\SupernoteBackups\20260916-142616_SUPERNOTE_BACKUP"
SupernoteBridge.exe --verify "D:\SupernoteBackups\20260916-142616_SUPERNOTE_BACKUP"
```

## 5. settings.json

Created automatically next to the exe on first run:

```json
{
  "port": 8099,
  "open": true,
  "backupRoot": null
}
```

| Key | Meaning |
|---|---|
| `port` | UI port |
| `open` | open browser on start |
| `backupRoot` | default backup parent folder; `null` = `BACKUP` folder beside the exe |

Command-line switches always override settings.json.

## 6. Scheduled backups (sn-backup.ps1)

A wrapper script with logging is in the repository at `scripts\sn-backup.ps1`:

```
powershell -ExecutionPolicy Bypass -File C:\path\to\sn-backup.ps1 -Target "C:\path\to\SupernoteBackups" -Exe "C:\path\to\SupernoteBridge.exe"
```

- Writes a log per run into a `logs` folder next to the script.
- Returns the app's exit code (0/1/2) — usable from Task Scheduler.
- Task Scheduler example: trigger *At log on* or daily, action `powershell.exe` with the arguments above.

## 7. REST API (for scripting)

The UI is a thin layer over a local REST API — you can call it yourself (e.g. from PowerShell) while the app runs:

| Endpoint | Description |
|---|---|
| `GET /api/device` | connection status, model, serial; `backup` = the open backup (root, serial, createdAt, files) or `null` |
| `POST /api/source/open` · `POST /api/source/device` | open a backup (native dialog) · back to the device |
| `POST /api/verify/start` · `/api/verify/cancel` · `GET /api/verify/status` | verify the open backup (status lists the problems) |
| `POST /api/restore/plan?path=` · `POST /api/restore/start?overwrite=` · `/api/restore/cancel` · `GET /api/restore/status` | restore from the open backup to the device: check first, then restore (overwrite or skip different files) |
| `GET /api/search?q=&under=` · `POST /api/search/refresh` · `GET /api/search/status` | search names + recognized handwriting of the current source (`under` = limit to a folder) |
| `GET /api/fs?path=<devicePath>` | list a device folder |
| `GET /api/file?path=` | download a file |
| `PUT /api/file?path=` | upload (raw body) to a full device path |
| `DELETE /api/file?path=&recursive=` | delete file/folder |
| `POST /api/rename?path=&newName=` | rename in place |
| `GET /api/note/meta?path=` · `/api/note/page?path=&n=&thumb=` | note page count / page PNG |
| `GET /api/note/export.pdf?path=&vector=true|false` · `/api/note/export.txt?path=` | note exports |
| `GET /api/pdf/meta?path=` · `/api/pdf/page?path=&n=` · `/api/pdf/export.pdf?path=` | annotated PDF preview/export |
| `POST /api/backup/start?root=` · `/api/backup/cancel` · `GET /api/backup/status` | backups |
| `POST /api/export/start?src=&target=&vector=` · `/api/export/cancel` · `GET /api/export/status` | bulk export |

In offline mode the same endpoints read from the open backup; write endpoints answer `403`. Device paths look like `\Internal shared storage\Document\...` or `\SanDisk SD card\...` (URL-encode them).

## 8. Privacy & security model

- Everything is local; the app never talks to the internet.
- Device content used for previews/exports is processed **in RAM only** and never written to disk. The only files ever written are the ones you explicitly ask for: downloads, exports, and backups.
- Preview responses carry `Cache-Control: no-store`, so the browser does not keep copies of device content in its disk cache either.
- Unavoidable OS-level caveat: like any program, the operating system may page process memory to the Windows pagefile.

## 9. Known limitations

- **Highlight-digest `.mark` files**: some PDFs annotated mostly with the text-highlighter store strokes as vector data (TOTALPATH) with empty bitmap layers. Those annotations are **not shown/exported** (deliberately descoped); the raw `.mark` file is still backed up byte-exact, so nothing is lost.
- The Supernote **To-Do app** data is not reachable over USB at all (device limitation); the To-Do feature was dropped.
- `\SanDisk SD card\Android\data` is app-private and cannot be read over MTP.
- One preview document is cached in RAM at a time; switching between two large documents re-loads them.
- Windows-only (the USB/MTP layer uses Windows Portable Devices).

## 10. Troubleshooting

| Symptom | Fix |
|---|---|
| Badge stays *disconnected* | Unlock the device screen; check the USB cable; the device must show in Explorer as "Supernote Nomad". Wait ~10 s after plugging in. |
| Browser shows "connection refused" | The exe isn't running, or a different `--port` was used. |
| Port already in use | Start with `--port <other>` or edit settings.json. |
| SmartScreen blocks the exe | *More info → Run anyway* (the exe is unsigned). |
| Backup ends with warnings | Usually just the unreadable `Android\data` folder — harmless. Check details via `GET /api/backup/status`. |
| Preview of a huge file is slow to open | Normal — the file is copied from the device into RAM first (USB speed). |
| Rename fails | Names cannot contain `\ / : * ? " < > |`. |

---
*SupernoteBridge is an unofficial tool, not affiliated with Ratta. Built on the open-source work of jya-dev/supernote-tool and nelinory/SupernoteSharp (rendering), Bassman2/MediaDevices (MTP), and PDFtoImage/PDFium (PDF rasterization).*
