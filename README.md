# WinTAKTracker

Lightweight **Windows tray app** that reports your position (PLI / self-SA CoT) to one or more TAK servers over TLS or TCP, and optionally broadcasts ATAK-compatible **Mesh SA** multicast on the LAN/VPN.

WinTAKTracker is **tracking-only**. It does not host a common operating picture. Use a companion map client to see yourself and others:

| Companion | Platform |
|-----------|----------|
| [CloudTAK](https://github.com/snstac/cloudtak) (or your org’s CloudTAK URL) | Browser |
| [ATAK-CIV](https://play.google.com/store/apps/details?id=com.atakmap.app.civ) / [TAK.gov](https://tak.gov) | Android |
| [TAK Aware](https://apps.apple.com/us/app/tak-aware/id6738631659) | iOS |
| [WinTAK](https://tak.gov) | Windows |

Not an official TAK Product Center application. TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners.

## Features

- **Multi-server TAK** — enroll several profiles, enable/disable per server, TLS (`ssl`) or cleartext TCP, auto-reconnect
- **Mesh SA** — ATAK-default UDP multicast (`239.2.3.1:6969`), always-on or only when disconnected
- **GPS** — USB NMEA serial, Windows Location, last-fix hold, and **network/IP geolocation** fallback (approximate)
- **Identity** — callsign (defaults to Windows computer name), team, role, ground/vehicle CoT type
- **Enrollment** — paste Portal / OpenTAK Tracker–style URLs, SoftCert ZIP, manual `.p12`, webcam QR scan
- **Reporting** — ATAK-style Dynamic or Constant rates (reliable servers vs unreliable mesh)
- **Ops** — system tray states, pause/mute outbound CoT, start with Windows, optional prevent-sleep while tracking
- **Updates** — check / auto-install from GitHub Releases (SHA256 verified)
- **Privacy** — config, DPAPI secrets, certs, and logs stay under `%LocalAppData%\WinTAKTracker\`

See [FEATURES.md](FEATURES.md) for the full status table, and the docs site: [Features](https://copixus.github.io/WinTAKTracker/features) · [Changelog](https://copixus.github.io/WinTAKTracker/changelog).

## Install (from Releases)

Download the latest self-contained build — no separate .NET runtime required:

**[https://github.com/CopIXus/WinTAKTracker/releases/latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)**

1. Grab `WinTAKTracker.exe` (optionally verify `WinTAKTracker.exe.sha256`).
2. Run it. Windows SmartScreen may warn on unsigned builds: **More info → Run anyway**.
3. Open **Settings** from the tray icon (left-click or context menu).

Every push to `main` publishes a Release with version `0.1.<run_number>` (git tag `build-0.1.<run_number>` so it does not re-trigger the `v*` workflow). Annotated SemVer tags (`v1.2.3`) still produce versioned releases.

## Setup / enrollment

1. Enroll a TAK server from **Settings → Servers**:
   - Paste an enrollment URL or iTAK CSV (fictional example shape only — never commit real URLs). Portal/`tak://…/enroll` links enroll a client certificate via Marti on port **8446**, then stream CoT on **8089** SSL:
     `tak://com.atakmap.app/enroll?host=tak.example.com&username=USER&token=TOKEN`
   - Enroll tokens are short-lived (~15 minutes) — paste and apply promptly.
   - Or **Scan QR…**, **Import SoftCert ZIP…**, or **Manual .p12 import…**
2. Confirm **Identity** (callsign defaults to this PC’s Windows name until enrollment or you set one).
3. Configure **GPS** (COM port / baud, Windows Location permission, optional network fallback).
4. Optionally set a **CloudTAK URL** under **View the map**, then open CloudTAK or a companion app to see the COP.
5. Leave the app running in the tray; use **Pause tracking** to mute outbound CoT without quitting.

Fictional samples for docs/tests (no real hosts/tokens):

- [`samples/enrollment.example.txt`](samples/enrollment.example.txt)
- [`samples/config.example.json`](samples/config.example.json)

## GPS and network location

| Source | When used | Notes |
|--------|-----------|--------|
| NMEA serial | USB GPS with COM port | Used when you select a COM port in Settings |
| Windows Location (Wi‑Fi/network) | Default without a USB GPS | OS Wi‑Fi / network positioning (browser-quality). Requires Windows Location services |
| Network IP (approximate) | Last resort only | [ipwho.is](https://ipwho.is/) over **HTTPS**, no API key; city/region scale (~25 km CE); Status labels it **Network IP (approximate)** |

**Without a GPS dongle:** enable **Settings → Privacy & security → Location** (Location services ON, and allow desktop apps to use your location), then use **Request Windows Location permission** in the app. IP geolocation is delayed until Windows Location has had a real chance to get a fix, and is not used while a Windows/NMEA fix is active.

Pause mutes outbound reporting; it does not invent precision. Windows Location CoT `ce` uses the OS-reported accuracy (often tens of meters with Wi‑Fi). Network IP fixes use estimated CoT `how` and a large `ce`.

## Settings

All options persist under `%LocalAppData%\WinTAKTracker\config.json` (secrets beside it as DPAPI blobs). The Settings UI **auto-saves** on change (checkboxes, combos, and fields on focus loss), including:

- Startup (Start with Windows, Prevent sleep)
- Identity, GPS, Reporting, Mesh SA
- CloudTAK URL, Updates auto-install, Diagnostics log level
- Per-server Enabled toggles

## Privacy

This public repository must **never** contain real TAK server hostnames, certificates, passwords, API/enrollment tokens, SoftCert packages, or live enroll URLs. Use obvious fakes only (`tak.example.com`, `USER`, `TOKEN`, `CALLSIGN`).

Operational data stays on the device. See [`SECURITY.md`](SECURITY.md), [`CONTRIBUTING.md`](CONTRIBUTING.md), and `.cursor/rules/no-tak-secrets.mdc`.

## Build from source

Requirements: Windows 10 1809+ or Windows 11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build WinTAKTracker.sln -c Debug
dotnet run --project src/WinTAKTracker
```

Self-contained single-file publish (`win-x64`):

```powershell
dotnet publish src/WinTAKTracker -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

CI (`.github/workflows/release.yml`) publishes `WinTAKTracker.exe` + SHA256 on every push to `main` and on `v*` tags.

## Docs and changelog

| Resource | Link |
|----------|------|
| Feature matrix | [FEATURES.md](FEATURES.md) · [docs site](https://copixus.github.io/WinTAKTracker/features) |
| Changelog | [CHANGELOG.md](CHANGELOG.md) · [docs site](https://copixus.github.io/WinTAKTracker/changelog) |
| Contributing | [CONTRIBUTING.md](CONTRIBUTING.md) |
| Security | [SECURITY.md](SECURITY.md) |
| Releases | [GitHub Releases](https://github.com/CopIXus/WinTAKTracker/releases) |

## License

Apache License 2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
