# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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

- Tray attaches to the service when present (no second in-process tracker); portable in-process mode remains when the service is absent
- Prefer Windows Location (Wi‑Fi/OS) over IP geolocation: high-accuracy Geolocator, retries, continuous updates while stationary, delayed IP fallback, and no auto-open of unrelated COM ports
- Status labels: **Windows Location (Wi‑Fi/network)** vs **Network IP (approximate)**; GPS settings document enabling Windows Location privacy toggles
- Fix reporting crash when Windows Location returns NaN speed/course (`TimeSpan` / CoT build)
- Settings UI auto-saves options; service mode pushes config over IPC to ProgramData
- Expanded project README (install, enrollment, GPS, privacy, companions, build, service)
- Server cards: status pill beside name; compact Remove / Connect·Disconnect / Test on the right
- Connected badge uses live stream state from the service over IPC (Test is a one-off probe and does not set Connected)
- On tray attach: finish LocalAppData→ProgramData secret re-protect, refresh statuses, reload enabled profiles
- Connect/Disconnect button label follows live Connected state

## [0.1.0] - 2026-07-30

### Added

- Initial public skeleton: WPF tray app shell, settings window, DPAPI-backed config under `%LocalAppData%\WinTAKTracker\`
- Pause service stub and tray icon state framework
- Apache-2.0 license, NOTICE attributions, SECURITY / CONTRIBUTING docs
- Sample enrollment and config placeholders (fictional hosts only)
- Secret-scan CI (Gitleaks), release and GitHub Pages workflows

[Unreleased]: https://github.com/CopIXus/WinTAKTracker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/CopIXus/WinTAKTracker/releases/tag/v0.1.0
