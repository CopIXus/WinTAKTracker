# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Default callsign uses the Windows computer name when unset (replaces hard-coded `WIN-TRACKER`)
- Network/IP geolocation fallback via ipwho.is (HTTPS) when NMEA and Windows Location have no fix
- App branding from `WinTAKTrackerLogo` assets (window, tray, EXE icon)
- GitHub Release on every push to `main` (`0.1.<run_number>`) in addition to SemVer `v*` tags

### Changed

- Prefer Windows Location (Wi‑Fi/OS) over IP geolocation: high-accuracy Geolocator, retries, continuous updates while stationary, delayed IP fallback, and no auto-open of unrelated COM ports
- Status labels: **Windows Location (Wi‑Fi/network)** vs **Network IP (approximate)**; GPS settings document enabling Windows Location privacy toggles
- Fix reporting crash when Windows Location returns NaN speed/course (`TimeSpan` / CoT build)
- Settings UI auto-saves options to `%LocalAppData%\WinTAKTracker\` on change
- Expanded project README (install, enrollment, GPS, privacy, companions, build)

## [0.1.0] - 2026-07-30

### Added

- Initial public skeleton: WPF tray app shell, settings window, DPAPI-backed config under `%LocalAppData%\WinTAKTracker\`
- Pause service stub and tray icon state framework
- Apache-2.0 license, NOTICE attributions, SECURITY / CONTRIBUTING docs
- Sample enrollment and config placeholders (fictional hosts only)
- Secret-scan CI (Gitleaks), release and GitHub Pages workflows

[Unreleased]: https://github.com/CopIXus/WinTAKTracker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/CopIXus/WinTAKTracker/releases/tag/v0.1.0
