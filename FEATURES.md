# Features

WinTAKTracker is a lightweight Windows tray app for **position location information (PLI)** — self-SA CoT to TAK Server(s) and ATAK-compatible Mesh SA. It is **tracking-only**; use CloudTAK, ATAK-CIV, TAK Aware, or WinTAK for the common operating picture.

Status legend: **Shipping** (in the current public build), **In progress**, **Planned** (v1 scope).

## Enrollment and identity

| Capability | Status |
|------------|--------|
| Paste Portal / OpenTAK Tracker–style enroll URLs (`opentaktracker://`, `tak://` enroll / preference / SoftCert import) | Planned |
| SoftCert / pref ZIP drop-in | Planned |
| Manual WinTAK-style `.p12` + CA import (TLS or cleartext TCP) | Planned |
| Webcam QR scan with paste-URL fallback | Planned |
| Manual identity edit (callsign, team, role, ground vs vehicle CoT type) | Planned |
| DPAPI-protected local config and certs under `%LocalAppData%` | Shipping |

## Multi-server TAK

| Capability | Status |
|------------|--------|
| Multiple servers with enable toggles and per-server status | Planned |
| TLS (`ssl`) and cleartext TCP (`tcp`) CoT streaming | Planned |
| Test server / wipe profile / forget all | Planned |
| Auto-reconnect with backoff after link loss, sleep/wake, or VPN flap | Planned |

## Mesh SA (LAN / VPN)

| Capability | Status |
|------------|--------|
| ATAK-compatible UDP multicast Mesh SA (always-on while tracking by default) | Planned |
| Mode: always with servers, or only when no TAK Server connected | Planned |
| Optional NIC selection for multi-homed machines | Planned |

## GPS and reporting

| Capability | Status |
|------------|--------|
| USB NMEA serial GPS | Shipping |
| Windows Location API fallback | Shipping |
| Network / IP geolocation fallback (approximate, ipwho.is HTTPS) | Shipping |
| Last-fix hold with honest stale / confidence | Shipping |
| Default callsign = Windows computer name when unset | Shipping |
| Adaptive (Dynamic) and Constant reporting rates (reliable vs unreliable) | In progress |
| Status GPS details (lat/lon, speed, course, altitude, accuracy) | In progress |

## Ops and UX

| Capability | Status |
|------------|--------|
| System tray icon with clear state tooltips | Shipping (framework) |
| Pause / mute all outbound CoT without quitting | Shipping (stub) |
| Start with Windows | Planned |
| Stay reporting while screen locked; optional prevent-sleep while tracking | Planned |
| Small OSM self-location preview (settings only; not a COP) | Planned |
| CloudTAK open + companion links (ATAK-CIV, TAK Aware, WinTAK) | Planned |
| Redacted diagnostics log and status export | Planned |
| In-app updates from GitHub Releases | Planned |

## Distribution

| Capability | Status |
|------------|--------|
| Self-contained single-file `win-x64` EXE | Shipping (publish profile) |
| Versioned GitHub Releases + SHA256 | Shipping (CI) |
| Features / Changelog docs site | Shipping |

## Privacy

- Runtime secrets, certs, and logs stay on the machine (`%LocalAppData%\WinTAKTracker\`).
- This public repository never contains real TAK hosts, tokens, certificates, or live enroll URLs — only fictional samples.
- See [SECURITY.md](SECURITY.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Out of scope (v1)

- Full multi-user COP, chat, or drawing inside this app
- Receiving / displaying other Mesh SA contacts on the preview map
- Offline OSM tile packs / custom imagery
- TAK Protocol protobuf mesh / QUIC (first ship is XML CoT + SSL/TCP + Mesh UDP)
- Simulated GPS
