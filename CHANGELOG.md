# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Settings: **Apply callsign/team from Portal** toggle (`ApplyRemoteIdentityFromPortal`, default on); Diagnostics **TLS soft-accept** opt-in (`AllowInsecureTlsSoftAccept`, default off)
- IPC `UnlockSettings` / `LockSettings`; settings lock passwords hashed (SHA-256 + salt) with plaintext migration on unlock
- Corrupt `config.json` quarantine (`config.json.corrupt-<ticks>`) via `LoadDetailed` — no silent overwrite on load
- Uninstall: remove HKCU Run value; optional prompt to delete `%ProgramData%\WinTAKTracker` (default No)
- README Status dashboard screenshot (illustrative Plymouth Rock sample) and architecture mermaid diagram
- Companion apps section with platform icons; **TAK.gov** link to https://tak.gov
- Portal / OpenTAK remote identity: device-profile sync + preference/SoftCert apply with **`.wtt`** callsign suffix and team color ([docs/remote-config.md](docs/remote-config.md))
- Optional CoT `remarks` with Windows computer name when callsign differs (Reporting setting, default on)
- Distinct input field backgrounds (TextBox/ComboBox) in light and dark themes
- **One-click Setup** — Inno Setup `WinTAKTracker-Setup.exe` installs service + tray under Program Files with a single UAC prompt; Start Menu / optional Desktop shortcuts; optional LocalAppData→ProgramData migrate; clean service uninstall ([docs/windows-service.md](docs/windows-service.md))
- **Windows Service** vertical slice: `WinTAKTracker.Core` + `WinTAKTracker.Service`, named-pipe IPC, `%ProgramData%` machine store with LocalMachine DPAPI, `scripts/install-service.ps1` ([docs/windows-service.md](docs/windows-service.md))
- **Computer vs per-user callsign** — computer identity when logged off; per-user identity when logged in; first-login callsign prompt
- Default callsign uses the Windows computer name when unset (replaces hard-coded `WIN-TRACKER`)
- Network/IP geolocation fallback via ipwho.is (HTTPS) when NMEA and Windows Location have no fix
- App branding from `WinTAKTrackerLogo` assets (window, tray, EXE icon)
- GitHub Release on every push to `main` (`0.1.<run_number>`) in addition to SemVer `v*` tags
- Optional Authenticode signing in release CI (Azure Artifact/Trusted Signing or PFX) when secrets are set; [docs/code-signing.md](docs/code-signing.md) for SmartScreen/SAC guidance
- Status **Mode** badge: Windows Service (IPC) vs Standalone
- System light/dark theme from Windows `AppsUseLightTheme` (runtime swap)

### Changed

- Status dashboard UI aligned with the design mock: sidebar icons, numbered status tiles with circular icons, mode banner, icon action buttons
- FEATURES.md / docs/features.md updated to match shipping capabilities (Mesh SA, Startup, GPS, pause, diagnostics, presence)

### Fixed

- **Callsign vs computer name on Portal/Server** — delay first PLI until user session can bind; re-announce when callsign changes; stop bare hostname in CoT remarks (optional `Computer: …`, default off)
- **TAK Server managed-device presence** — announce PLI on connect; ATAK-shaped self-SA (`endpoint`, `uid@Droid`, `precisionlocation`); reply to `t-x-c-t` pings; new installs use `WINDOWS-WinTAKTracker-*` device UIDs ([docs/managed-devices.md](docs/managed-devices.md))
- **Schannel mTLS after enroll** — load client `.p12` with `MachineKeySet`/`UserKeySet` (not `EphemeralKeySet`); Windows Schannel cannot use ephemeral private keys for `SslStream`, which looked like “client certificate rejected” after a successful Portal enroll
- **Private-CA TLS after enroll** — persist/load the full Marti CA chain in the trust store (not only `ca0`); clearer errors when the *server* cert is untrusted vs client cert rejected; tray can write `certs`/`secrets` again for enroll; soft-accept toggle reloads connections
- **No double tracker** — when the Windows Service is installed, tray retries IPC attach (backoff) and never falls back to in-process `Core.StartAsync` if the service is unreachable (companion-only)
- **Safe config load** — parse failures quarantine the file; TrackingHost ctor does not Save over corrupt `config.json`
- **IPC hardening** — mutating methods require an interactive pipe client; settings lock gates `SetConfig`/identity; one active companion SID for GPS/session; clear companion GPS on logoff
- **SetConfig ≠ full reload** — connection reload only when servers/GPS/mesh-relevant fields change
- **Service mode GPS** — skip WinRT Geolocator under LocalSystem (NMEA + companion + optional IP only)
- **Async tray IPC** — `SaveConfigAsync` / pause / tray refresh avoid UI-thread `.GetResult()` where practical
- **Reporting** — serialize CoT sends with timeout; dispose client certs after TLS sessions
- **fail2ban-safe reconnect** — stop auto-reconnect after a few TLS/cert (or limited network) failures so infra-TAK’s TAK Server jail (~20 TLS fails / 5 min) does not ban the client IP; show a detailed Error on the server card with fix steps ([docs/fail2ban.md](docs/fail2ban.md))
- **Update now** for Setup/service installs: download elevated `WinTAKTracker-Setup.exe` (UAC) instead of a silent portable EXE replace that could not overwrite Program Files / the running service; only quit after the installer or replace helper is armed; show errors if apply fails; auto-update balloon says Setup started (not install success)

### Changed

- Machine store ACL: Authenticated Users Modify on root/logs/updates; **secrets/certs SYSTEM+Admins only** ([SECURITY.md](SECURITY.md), [docs/windows-service.md](docs/windows-service.md))
- New-config defaults: Mesh `OnlyWhenDisconnected`; `EnableNetworkFallback = false`; reporting min intervals ≥ **5s**; TLS soft-accept off
- Attach path: prefer service-authoritative `GetConfig`; `SetConfig` only when migration changed something (single reload max)
- Relicensed to **WinTAKTracker Free Application License 1.0** (source available, free to use; no selling the app; paid install/support OK). Not OSI Open Source. Prior Apache-2.0 releases remain under Apache
- Removed CloudTAK URL field and tray “Open CloudTAK” (use Companion apps for map clients)
- Start with Windows always registers the tray Run key (portable + service companion); best-effort service start on tray launch; per-user callsign prompt on each tray start when unset
- Tray attaches to the service when present (no second in-process tracker); portable in-process mode remains when the service is absent
- Prefer Windows Location (Wi‑Fi/OS) over IP geolocation: high-accuracy Geolocator, retries, continuous updates while stationary, delayed IP fallback, and no auto-open of unrelated COM ports
- Status labels: **Windows Location (Wi‑Fi/network)** vs **Network IP (approximate)**; GPS settings document enabling Windows Location privacy toggles
- Fix reporting crash when Windows Location returns NaN speed/course (`TimeSpan` / CoT build)
- Settings UI auto-saves options; service mode pushes config over IPC to ProgramData
- Expanded project README (install, enrollment, GPS, privacy, companions, build, service)
- Full light/dark theme: ComboBox dropdowns, scrollbars, buttons, Callsign/QR/password dialogs; themed in-app dialogs
- Compact server rows: Connect checkbox · host · protocol:port · status pill · Test / ✕
- Startup shows Windows Service status (Running / Stopped / Not installed) and Mode (Service vs Standalone)
- Default log level **Error**; max log size default **30 MB** with rotation/trim
- Updates: inline current/latest/status/last-checked — no popup for check / up-to-date
- About: crisp logo + **CopIX LLC**; assembly / Setup publisher metadata CopIX LLC
- Connected badge uses live stream state from the service over IPC (Test is a one-off probe and does not set Connected)
- On tray attach: finish LocalAppData→ProgramData secret re-protect, refresh statuses, reload enabled profiles

## [0.1.0] - 2026-07-30

### Added

- Initial public skeleton: WPF tray app shell, settings window, DPAPI-backed config under `%LocalAppData%\WinTAKTracker\`
- Pause service stub and tray icon state framework
- Apache-2.0 license, NOTICE attributions, SECURITY / CONTRIBUTING docs
- Sample enrollment and config placeholders (fictional hosts only)
- Secret-scan CI (Gitleaks), release and GitHub Pages workflows

[Unreleased]: https://github.com/CopIXus/WinTAKTracker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/CopIXus/WinTAKTracker/releases/tag/v0.1.0
