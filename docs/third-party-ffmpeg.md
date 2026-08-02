---
title: FFmpeg (third-party)
---

# FFmpeg (third-party)

Video encode/stream/record in WinTAKTracker uses **[FFmpeg](https://ffmpeg.org/)** as an **external** program (`ffmpeg.exe`). WinTAKTracker does not link against FFmpeg libraries; it starts FFmpeg as a child process.

## How operators get it

1. **Setup installs** (`WinTAKTracker-Setup.exe`) — CI may bundle a pinned Windows *essentials* build next to `WinTAKTracker.exe` (see release workflow + `scripts/fetch-ffmpeg.ps1`).
2. **Already on PATH** — e.g. `winget install "FFmpeg (Essentials Build)"` or Chocolatey `ffmpeg`.
3. **Manual** — download a Windows build from [gyan.dev FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/), then either:
   - set **Settings → Video → FFmpeg path**, or
   - place `ffmpeg.exe` next to `WinTAKTracker.exe`, or
   - place it under `%LocalAppData%\WinTAKTracker\tools\ffmpeg.exe`.

## License note

The essentials builds from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) are distributed under **GPLv3**. When Setup bundles `ffmpeg.exe`, a `THIRD_PARTY_FFMPEG.txt` notice is installed beside it. Shipping FFmpeg as a separate executable invoked by process is treated as aggregation with that third-party program; WinTAKTracker’s own license is unchanged. Not legal advice — see upstream FFmpeg / gyan.dev terms.
