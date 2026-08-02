---
title: Features
---

# Features

WinTAKTracker is a lightweight Windows tray app for **position location information (PLI)** — self-SA CoT to TAK Server(s) and ATAK-compatible Mesh SA. It is **tracking-only**; use CloudTAK, ATAK-CIV, TAK Aware, or WinTAK for the common operating picture.

[← Home](index.md) · [Changelog](changelog.md) · [Download latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)

Status legend: **Shipping** (in the current public build), **Planned** (future).

## Enrollment and identity

| Capability | Status |
|------------|--------|
| Paste Portal / OpenTAK Tracker–style enroll URLs (`opentaktracker://`, `tak://` enroll / preference / SoftCert import) | Shipping |
| SoftCert / pref ZIP drop-in | Shipping |
| Manual WinTAK-style `.p12` + CA import (TLS or cleartext TCP) | Shipping |
| Webcam QR scan with paste-URL fallback | Shipping |
| Manual identity edit — computer callsign + per-user callsign/team/role/phone | Shipping |
| Sticky last-user callsign after logoff (opt-in revert to computer callsign) | Shipping |
| First-login callsign prompt (skip → sticky/computer until set in Settings) | Shipping |
| DPAPI-protected config (LocalAppData / ProgramData for service) | Shipping |
| Windows Service always-on host + tray IPC companion | Shipping |
| One-click Setup (`WinTAKTracker-Setup.exe`) — service + tray, elevated install | Shipping |
| Portal / device-profile remote callsign (`.wtt`) + team color | Shipping |
| User callsign preferred over computer name for TAK Server/Portal presence | Shipping |

## Multi-server TAK

| Capability | Status |
|------------|--------|
| Multiple servers with enable toggles and per-server status | Shipping |
| TLS (`ssl`) and cleartext TCP (`tcp`) CoT streaming | Shipping |
| Test server / wipe profile / forget all | Shipping |
| Auto-reconnect with backoff after link loss, sleep/wake, or VPN flap | Shipping |
| Circuit breaker to avoid fail2ban reconnect storms | Shipping |
| Immediate presence PLI + ATAK-shaped self-SA | Shipping |
| Server ping reply; Schannel-safe certs; full CA trust store | Shipping |

## Video streaming (ICU-inspired)

| Capability | Status |
|------------|--------|
| Settings → Video setup + Video Console (previews, Start/Stop, Stay on top) | Shipping |
| Tray camera indicator; open Console on startup (optional) | Shipping |
| FFmpeg: on-device RTSP, push URL, UDP MPEG-TS | Shipping |
| Self-SA `__video` + FOV sensor CoT | Shipping |
| FOV aim viewer + GPS course offset | Shipping |
| Hotkey / audio cues; 5‑min MP4 + SHA-256 + KML recording | Shipping |

## Mesh SA (LAN / VPN)

| Capability | Status |
|------------|--------|
| ATAK-compatible UDP multicast Mesh SA | Shipping |
| Mode: Always, or OnlyWhenDisconnected | Shipping |
| Optional NIC selection for multi-homed machines | Shipping |

## GPS and reporting

| Capability | Status |
|------------|--------|
| USB NMEA serial GPS | Shipping |
| Windows Location + tray→service companion bridge | Shipping |
| Network / IP geolocation fallback (approximate) | Shipping |
| Last-fix hold with honest stale / confidence | Shipping |
| Adaptive (Dynamic) and Constant reporting rates | Shipping |
| Status dashboard GPS details + OSM self-map preview | Shipping |

## Ops and UX

| Capability | Status |
|------------|--------|
| System tray icon with version + state tooltips | Shipping |
| Pause / mute all outbound CoT without quitting | Shipping |
| Start with Windows; optional prevent-sleep while tracking | Shipping |
| Status tile dashboard with icons | Shipping |
| Companion app links with platform icons | Shipping |
| Redacted diagnostics log and status JSON export | Shipping |
| Settings lock; system light/dark theme | Shipping |
| In-app updates from GitHub Releases | Shipping |

## Distribution

| Capability | Status |
|------------|--------|
| Self-contained single-file `win-x64` EXE | Shipping |
| Versioned GitHub Releases + SHA256 | Shipping |
| Features / Changelog docs site | Shipping |

## Privacy

- Runtime secrets, certs, and logs stay on the machine (`%LocalAppData%` portable; `%ProgramData%\WinTAKTracker` for the Windows Service).
- This public repository never contains real TAK hosts, tokens, certificates, or live enroll URLs — only fictional samples.

## Planned (post–beta)

| Capability | Notes |
|------------|--------|
| Portal Send Configuration UI for WinTAKTracker | [portal-send-config-wintaktracker.md](portal-send-config-wintaktracker.md) |
| Richer inbound CoT (beyond ping/pong) | Chat / packages remain out of scope for v1 |

## Out of scope (v1)

- Full multi-user COP, chat, or drawing inside this app
- Receiving / displaying other Mesh SA contacts on the preview map
- Offline OSM tile packs / custom imagery
- TAK Protocol protobuf mesh / QUIC
- Simulated GPS

Canonical copy: [`FEATURES.md`](https://github.com/CopIXus/WinTAKTracker/blob/main/FEATURES.md) in the repository.
