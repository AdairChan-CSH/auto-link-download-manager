# All Links Downloader (C#)

A WinForms app that collects every link from pasted HTML or a fetched web page, then downloads
the selected files with parallel HTTP Range segments. Downloads can be paused, the app closed,
and resumed later, including after a restart.

The Visual Basic port lives in `..\link-downloader-vb`. Both builds read the same settings file
and the same `.alllinks` metadata format, so either one can resume downloads started by the other.

## Requirements

- Windows with .NET Framework 4.8.
- No SDK or Visual Studio needed to run the compiled exe.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Uses the in-box .NET Framework compiler (`csc.exe`) and produces `LinkDownloader.exe`.

Build and run the self-tests (link extraction, segmented pause/restart/resume with hash check,
fallback for servers without Range support, and restart over a stale partial file when the
size is unknown):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Tests
```

## Use

1. **Collect** — paste HTML/page source or a plain URL list into the left pane, or type a page
   address and press **Fetch page**. Click **Extract links**.
2. **Filter** — narrow the list by text, file type, or "Same host". Use the checkboxes, **All**,
   **None**, or double-click a row to toggle it.
3. **Queue** — choose **Add checked to queue**. Downloads start immediately unless
   **Start immediately** is cleared.
4. **Control** — **Pause**, **Resume**, **Pause all**, **Start all**, or double-click a queue row.
   Closing the window pauses and persists everything.
5. **Save As** — save one finished file wherever you like, or **Save done...** to copy every
   finished file to a folder.
6. **Resume later** — reopen the app. Unfinished downloads are listed as paused and can be
   resumed. Enable **Resume on launch** to start them automatically.

## How downloads work

- A HEAD/Range probe learns the size, ETag/Last-Modified, and whether byte ranges are allowed.
- Files that support ranges are split into 1–8 segments and downloaded in 4 MB chunks with
  bounded retries. Segments write at their own offsets in a preallocated partial file.
- Servers that ignore Range are downloaded as a single stream.
- If the remote file changes between sessions (size/ETag/Last-Modified mismatch), partial data
  is discarded and the file restarts once.
- Compressed transfer encoding is disabled for file downloads so Range offsets stay valid.

State lives beside the downloads:

```
<output folder>\
  <finished files>
  .alllinks\
    <id>.json   resume metadata
    <id>.part   partial data
```

## Settings

`%APPDATA%\AllLinksDownloader\settings.json` stores the output folder, parallel file limit,
segments per file, retry count, auto-resume, and optional extra request headers (one
`Name: value` per line).

## Source layout

| File | Purpose |
| --- | --- |
| `Program.cs` | Entry point, global error handling. |
| `MainForm.cs` | Layout, theme controls, lifetime. |
| `MainForm.Actions.cs` | Link collection/filtering, settings dialogs, queue actions. |
| `MainForm.Queue.cs` | Queue grid refresh, speed/ETA, painted progress bars. |
| `DownloadManager.cs` | Queue scheduler, probes, segment workers, persistence. |
| `DownloadTask.cs` | Serialized task and segment state. |
| `LinkExtractor.cs` | HTML attribute/URL extraction and normalization. |
| `HttpUtil.cs` | Filenames, categories, formatting, headers, Downloads folder. |
| `AppSettings.cs` | Persisted settings. |
| `JsonStore.cs` | Atomic JSON save/load via `DataContractJsonSerializer`. |
| `Theme.cs` | Colors, flat buttons, dark grid, custom progress bar. |
| `Tests.cs` | Console self-tests with a local Range-capable test server. |

## Notes

- Only `http` and `https` links are collected; `mailto:`, `javascript:`, fragments, `data:`,
  and similar entries are skipped.
- HLS/`.m3u8` playlists are listed like any other link but are not assembled into a video.
- The app does not bypass authentication, paywalls, or DRM.
