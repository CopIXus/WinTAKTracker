---
title: Changelog
---

# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

[← Home](index.md) · [Features](features.md) · [Download latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)

## [Unreleased]

### Added

- One-click **`WinTAKTracker-Setup.exe`** (Inno Setup) — service + tray, single UAC prompt ([Windows Service](windows-service.md))
- Windows Service + Core library + named-pipe IPC; computer vs per-user callsign ([Windows Service](windows-service.md))
- Optional Authenticode signing in release CI when secrets are set; [Code signing](code-signing.md) for SmartScreen / Smart App Control
- Status Mode badge (Windows Service vs Standalone); system light/dark theme

### Changed

- Tray attaches to service when present; portable in-process mode when absent
- Prefer Windows Location (Wi‑Fi/OS) over IP geolocation; delayed IP fallback; Status labels distinguish Wi‑Fi vs Network IP
- Server card layout + Connected badge from live IPC stream state; migrate/reconnect assist after Setup

## [0.1.0] - 2026-07-30

### Added

- Initial public skeleton: WPF tray app shell, settings window, DPAPI-backed config under `%LocalAppData%\WinTAKTracker\`
- Pause service stub and tray icon state framework
- Apache-2.0 license, NOTICE attributions, SECURITY / CONTRIBUTING docs
- Sample enrollment and config placeholders (fictional hosts only)
- Secret-scan CI (Gitleaks), release and GitHub Pages workflows

[Unreleased]: https://github.com/CopIXus/WinTAKTracker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/CopIXus/WinTAKTracker/releases/tag/v0.1.0

Canonical copy: [`CHANGELOG.md`](https://github.com/CopIXus/WinTAKTracker/blob/main/CHANGELOG.md) in the repository.
