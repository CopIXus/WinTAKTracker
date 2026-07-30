# WinTAKTracker

Lightweight Windows tray app that reports your position (PLI) to TAK over TLS/TCP and ATAK-compatible Mesh SA multicast. Settings open from the system tray; this app is **tracking-only** — use CloudTAK, ATAK, TAK Aware, or WinTAK for the common operating picture.

**Status:** early skeleton (Phase 1). GPS, enrollment, and networking land in later phases.

## Install (from Releases)

Download the latest self-contained build — no separate .NET install required:

**[https://github.com/CopIXus/WinTAKTracker/releases/latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)**

Grab `WinTAKTracker.exe` (and optionally verify `WinTAKTracker.exe.sha256`). Windows SmartScreen may warn on unsigned builds: **More info → Run anyway**.

Docs site: [Features](https://copixus.github.io/WinTAKTracker/features) · [Changelog](https://copixus.github.io/WinTAKTracker/changelog) (also [FEATURES.md](FEATURES.md) / [CHANGELOG.md](CHANGELOG.md) in-repo).

## Requirements

- Windows 10 1809+ or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source

## Build

```powershell
dotnet build WinTAKTracker.sln -c Debug
```

Run (tray + settings shell):

```powershell
dotnet run --project src/WinTAKTracker
```

Self-contained single-file publish (`win-x64`):

```powershell
dotnet publish src/WinTAKTracker -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Tagged releases (`v*`) are built by GitHub Actions and attached to [GitHub Releases](https://github.com/CopIXus/WinTAKTracker/releases).

## Config (local only)

Runtime data lives under `%LocalAppData%\WinTAKTracker\` (config, DPAPI-protected secrets, certs, logs). **Never commit** that folder or real enrollment material.

Fictional samples for docs/tests:

- [`samples/enrollment.example.txt`](samples/enrollment.example.txt)
- [`samples/config.example.json`](samples/config.example.json)

## Privacy

This repository must not contain real TAK servers, certificates, tokens, or enrollment URLs. See [`SECURITY.md`](SECURITY.md), [`CONTRIBUTING.md`](CONTRIBUTING.md), and `.cursor/rules/no-tak-secrets.mdc`.

Operational data stays on the device; only application source, docs, CI, and placeholder samples belong in git.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

TAK / ATAK / WinTAK / CloudTAK / TAK Aware are trademarks of their respective owners. WinTAKTracker is an independent project, not an official TAK Product Center app.
