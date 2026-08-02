---
title: Video streaming
---

# Video streaming (ICU-inspired)

WinTAKTracker can turn a laptop or USB camera into a live TAK video source: encode with **FFmpeg**, advertise a playable URL via CoT (`<__video>` + FOV `<sensor>`), and optionally record segmented MP4 + hash + KML.

This is **inspired by** community ICU-style plugins; the implementation is original to WinTAKTracker.

## Surfaces

| Surface | Role |
|---------|------|
| **Settings → Video** | Setup: cameras, tags, transport, encode, FOV aim viewer, hotkey, audio, recording, FFmpeg path, open-on-startup |
| **Video Console** | Ops: previews, Start/Stop, Stay on top |
| **Tray** | Camera badge when configured; LIVE accent while streaming; **Video Console…** menu |

Video runs in the **interactive tray session** only. After Windows logoff, streams stop; PLI can continue via the Windows Service.

## Transports

- **OnDeviceRtsp** — FFmpeg RTSP listen (default port `8554`, path `/live-{tag}`)
- **Push** — RTSP/RTMP URL to a restreamer (e.g. MediaMTX)
- **UdpMulticast** — MPEG-TS to a media multicast group (default `239.2.3.2:5004`, separate from CoT Mesh `239.2.3.1:6969`)

## CoT

While LIVE, outbound self-SA includes `__video`, `ConnectionEntry`, `sensor`, and `device` details. Optional `b-m-p-s-p-loc` sensor markers refresh on a timer for clients that draw FOV cones from that type.

## Recording

When enabled, each ~5‑minute segment produces:

1. `.mp4` — video
2. `.sha256` — content hash of that segment
3. `.kml` — GPS track sampled every 5 seconds

Filename pattern (sanitized):  
`YYYY-MMDD_HHmmssZ_HHmmss_Computer_Callsign_User[_tag]_NNN.mp4`

Folder size limit: delete oldest files (default) or stop recording.

## Prerequisites

- [FFmpeg](https://ffmpeg.org/) on PATH, `ffmpeg.exe` next to WinTAKTracker, or an absolute path in Settings → Video
- Camera accessible to the logged-on user (DirectShow)
