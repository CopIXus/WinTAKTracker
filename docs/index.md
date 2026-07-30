---
title: WinTAKTracker
---

# WinTAKTracker

Lightweight Windows tray app that reports your position (PLI) to TAK over TLS/TCP and ATAK-compatible Mesh SA multicast. Settings open from the system tray. This app is **tracking-only** — use CloudTAK, ATAK, TAK Aware, or WinTAK for the common operating picture.

## Download

**[Download the latest release](https://github.com/CopIXus/WinTAKTracker/releases/latest)** — self-contained `WinTAKTracker.exe` for Windows 10/11 (x64). No separate .NET install required.

**Unsigned** downloads may trip SmartScreen or Windows 11 Smart App Control. Workarounds and the Authenticode plan: [Code signing](code-signing.md). Release CI signs only when secrets are configured — do not assume a build is signed until you verify.

## Docs

- [Features](features.md) — what ships today and what is planned
- [Changelog](changelog.md) — release history (Keep a Changelog)
- [Code signing](code-signing.md) — SmartScreen / SAC, workarounds, Azure Trusted Signing & CI secrets

## Privacy

Enrollment credentials, certificates, and live TAK connection details stay on your PC under `%LocalAppData%\WinTAKTracker\`. The public repository never contains real servers, tokens, or certs — only fictional samples.

Source: [github.com/CopIXus/WinTAKTracker](https://github.com/CopIXus/WinTAKTracker)

## License

Apache License 2.0. TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners. WinTAKTracker is an independent project, not an official TAK Product Center app.
