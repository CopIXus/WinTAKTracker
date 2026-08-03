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

- **OnDeviceRtsp** — FFmpeg RTSP listen (default port `8554`, path `/live-{tag}`). **Best for ATAK/iTAK on the same LAN.**
- **Push** — RTSP (or RTMP) publish to a restreamer such as [TAK Video Restreamer](https://github.com/raytheonbbn/tak-video-restreamer) / MediaMTX
- **UdpMulticast** — MPEG-TS to a media multicast group (default `239.2.3.2:5004`, separate from CoT Mesh `239.2.3.1:6969`). CoT advertises `udp://@group:port` for players; many Wi‑Fi access points filter multicast.

### Push to TAK Video Restreamer

Use the restreamer dashboard **Quick Connect → RTSP SERVER** base URL (host + port, path empty or `/`).

1. Settings → Video → Mode = **Push** (not OnDeviceRtsp — that advertises your PC LAN IP as `/live-{tag}`)
2. **Push URL** = Quick Connect base, e.g. `rtsp://stream.example.com:8554/` (optional username/password if MediaMTX publish auth is enabled)
3. Each feed **Tag** is the stream name — WinTAKTracker publishes to `rtsp://stream.example.com:8554/{tag}`
4. Stop any LIVE feed, then Go LIVE again — Video Console should show `LIVE ×1 (Push) — rtsp://stream.example.com:8554/{tag}`
5. Confirm the stream under **Active Streams** on the restreamer UI; ATAK plays the same CoT URL
6. If Console still shows `rtsp://YOUR-LAN-IP:8554/live-…`, Mode is still On-device — re-select **Push**, click another field so it saves, restart LIVE

If you paste a full path already (e.g. `rtsp://stream.example.com:8554/custom`), that path is used as-is and the Tag is not appended.

### Viewing the feed

1. Go LIVE in Video Console (status shows the playable URL).
2. **On-device RTSP (recommended for ATAK):** on another device open `rtsp://PC-LAN-IP:8554/live-{tag}` in VLC, or in ATAK use **Video → +** / the self-SA video affordance. Allow inbound TCP `8554` on the PC firewall.
3. **Push / restreamer:** open the advertised `rtsp://…:8554/{tag}` URL (same path FFmpeg published).
4. **UDP multicast:** open `udp://@239.2.3.2:5004` in VLC on the same L2 network. If VLC fails, the AP is likely blocking multicast — switch to On-device RTSP or Push.
5. Video bytes do **not** go through TAK Server; only the CoT URL does. The ATAK device must reach the stream source (or multicast group) directly.

## CoT

While LIVE, outbound self-SA includes `<__video url="…"><ConnectionEntry …/></__video>` (ConnectionEntry nested — required by ATAK), plus `sensor` / `device`. RTSP uses `rtspReliable="1"` (TCP) so phones can play MediaMTX / TAK Video Restreamer over WAN. Optional `b-m-p-s-p-loc` sensor markers refresh on a timer for FOV cones.

**Video Console while LIVE (Push):** the webcam is exclusive to FFmpeg, so the console pulls a second viewer from the restreamer play URL (same path ATAK uses). Give it ~1–2s after Go LIVE for frames to appear.

## Recording

When enabled, each ~5‑minute segment produces:

1. `.mp4` — video
2. `.sha256` — content hash of that segment
3. `.kml` — GPS track sampled every 5 seconds

Filename pattern (sanitized):  
`YYYY-MMDD_HHmmssZ_HHmmss_Computer_Callsign_User[_tag]_NNN.mp4`

Folder size limit: delete oldest files (default) or stop recording.

## Prerequisites

- **FFmpeg** — external encoder (not part of the .NET app binary). Sources:
  - Bundled with **`WinTAKTracker-Setup.exe`** when CI fetches essentials (`ffmpeg.exe` beside the tray app)
  - [gyan.dev Windows builds](https://www.gyan.dev/ffmpeg/builds/) (Settings → Video → **Download FFmpeg…**)
  - `winget install "FFmpeg (Essentials Build)"` (Settings → Video → **winget install…**)
  - Custom path, next to `WinTAKTracker.exe`, or `%LocalAppData%\WinTAKTracker\tools\ffmpeg.exe`
- Details / license: [FFmpeg (third-party)](third-party-ffmpeg.md)
- Camera accessible to the logged-on user (DirectShow)
