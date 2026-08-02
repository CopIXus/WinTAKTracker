# Features

WinTAKTracker is a lightweight Windows tray app for **position location information (PLI)** — self-SA CoT to TAK Server(s) and ATAK-compatible Mesh SA. It is **tracking-only**; use CloudTAK, ATAK-CIV, TAK Aware, or WinTAK for the common operating picture.

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
| DPAPI-protected config (LocalAppData CurrentUser; ProgramData LocalMachine for service) | Shipping |
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
| Immediate presence PLI + ATAK-shaped self-SA (`endpoint`, `uid@Droid`, `takv`) | Shipping |
| Server `t-x-c-t` ping reply (`t-x-c-t-r`) | Shipping |
| Schannel-safe client cert load (`MachineKeySet` / `UserKeySet`) | Shipping |
| Full Marti CA chain in trust store; TLS soft-accept opt-in | Shipping |

## Mesh SA (LAN / VPN)

| Capability | Status |
|------------|--------|
| ATAK-compatible UDP multicast Mesh SA | Shipping |
| Mode: Always, or OnlyWhenDisconnected (default for new installs) | Shipping |
| Optional NIC selection for multi-homed machines | Shipping |

## GPS and reporting

| Capability | Status |
|------------|--------|
| USB NMEA serial GPS | Shipping |
| Windows Location (Wi‑Fi/OS); tray→service companion bridge | Shipping |
| Network / IP geolocation fallback (approximate; default off for new installs) | Shipping |
| Last-fix hold with honest stale / confidence | Shipping |
| Default callsign = Windows computer name when unset | Shipping |
| Adaptive (Dynamic) and Constant reporting rates (reliable vs unreliable) | Shipping |
| Status dashboard GPS details (lat/lon, speed, course, altitude, accuracy) | Shipping |
| OSM self-location preview (settings only; not a COP) | Shipping |

## Ops and UX

| Capability | Status |
|------------|--------|
| System tray icon with version + state tooltips | Shipping |
| Pause / mute all outbound CoT without quitting | Shipping |
| Start with Windows (tray Run key) | Shipping |
| Stay reporting while screen locked; optional prevent-sleep while tracking | Shipping |
| Status tile dashboard (mode, tracking, GPS, servers, mesh, callsign, map) | Shipping |
| Companion app links (ATAK / iTAK / WinTAK / TAK.gov) with platform icons | Shipping |
| Redacted diagnostics log and status JSON export | Shipping |
| Settings lock password (hashed) over IPC | Shipping |
| System light/dark theme from Windows | Shipping |
| In-app updates from GitHub Releases (Setup + UAC for service installs; portable EXE swap) | Shipping |

## Distribution

| Capability | Status |
|------------|--------|
| Self-contained single-file `win-x64` EXE | Shipping |
| Versioned GitHub Releases + SHA256 | Shipping |
| Features / Changelog docs site | Shipping |
| Optional Authenticode / Trusted Signing in CI | Shipping (when secrets set) |

## Privacy

- Runtime secrets, certs, and logs stay on the machine (`%LocalAppData%` portable; `%ProgramData%\WinTAKTracker` for the Windows Service).
- This public repository never contains real TAK hosts, tokens, certificates, or live enroll URLs — only fictional samples.
- See [SECURITY.md](SECURITY.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Planned (post–beta)

| Capability | Notes |
|------------|--------|
| Portal Connected Users parity without reconnect | Needs Portal to treat `WinTAKTracker` like ATAK for Send Configuration — see [docs/portal-send-config-wintaktracker.md](docs/portal-send-config-wintaktracker.md) |
| Richer inbound CoT handling | Beyond ping/pong (chat / data packages remain out of scope for v1) |

## Out of scope (v1)

- Full multi-user COP, chat, or drawing inside this app
- Receiving / displaying other Mesh SA contacts on the preview map
- Offline OSM tile packs / custom imagery
- TAK Protocol protobuf mesh / QUIC (first ship is XML CoT + SSL/TCP + Mesh UDP)
- Simulated GPS
