---
title: WinTAKTracker
---

# WinTAKTracker

Windows TAK PLI tracker: tray UI plus optional always-on **Windows Service**. Reports position over TLS/TCP and ATAK-compatible Mesh SA. **Tracking-only** — use CloudTAK, ATAK, TAK Aware, or WinTAK for the COP.

## Download

**[Download the latest release](https://github.com/CopIXus/WinTAKTracker/releases/latest)** — preferred: **`WinTAKTracker-Setup.exe`** (service + tray, one UAC prompt). Portable `WinTAKTracker.exe` also available. Windows 10/11 x64; no separate .NET install required.

**Unsigned** downloads may trip SmartScreen or Windows 11 Smart App Control. Workarounds and the Authenticode plan: [Code signing](code-signing.md). Release CI signs only when secrets are configured — do not assume a build is signed until you verify.

## Docs

- [Features](features.md) — what ships today and what is planned
- [Changelog](changelog.md) — release history (Keep a Changelog)
- [Code signing](code-signing.md) — SmartScreen / SAC, workarounds, Azure Trusted Signing & CI secrets
- [Always-on / Windows Service](windows-service.md) — service install, IPC, computer vs per-user callsign
- [Managed devices (TAK Server / Portal)](managed-devices.md) — how PLI identifies WinTAKTracker vs ATAK/CloudTAK
- [Beta testing](beta-testing.md) — checklist for group testers and GitHub Issues
- [Portal: Send Configuration for WinTAKTracker](portal-send-config-wintaktracker.md) — handoff for Portal developers (ATAK-parity)
- [fail2ban](fail2ban.md) — infra-TAK TLS probe bans and WinTAKTracker reconnect limits

## Privacy

Portable mode stores data under `%LocalAppData%\WinTAKTracker\`; service mode uses `%ProgramData%\WinTAKTracker\`. The public repository never contains real servers, tokens, or certs — only fictional samples.

Source: [github.com/CopIXus/WinTAKTracker](https://github.com/CopIXus/WinTAKTracker)

## License

**WinTAKTracker Free Application License 1.0** — source available, free to use; no charging for the software; paid install/support services OK. Not an OSI Open Source license. See [LICENSE](https://github.com/CopIXus/WinTAKTracker/blob/main/LICENSE). Prior Apache-2.0 releases remain under Apache. Not legal advice.

TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners. WinTAKTracker is an independent project, not an official TAK Product Center app.
