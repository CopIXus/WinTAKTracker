# Beta testing guide

Thank you for helping test WinTAKTracker. Use this checklist and report problems in **GitHub Issues** (not Slack threads alone).

## Get the build

1. Download the latest **`WinTAKTracker-Setup.exe`** from  
   [https://github.com/CopIXus/WinTAKTracker/releases/latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)
2. Prefer Setup (service + tray) over the portable EXE for always-on testing.
3. Note the version shown in the window title (e.g. `WinTAKTracker 0.1.x — Settings`).

## Quick path (15 minutes)

1. Install Setup (one UAC prompt). Confirm the tray icon appears and **Status** shows Mode = Service (or Standalone if you used portable).
2. **Servers** → paste a fresh Portal enroll URL (token ~15 minutes) → **Apply enrollment**. Expect “Certificate enrolled…”.
3. Confirm the profile shows **Connected** (or Test passes). Soft-accept under Diagnostics only if you hit a private-CA trust error.
4. **Identity** → set **My callsign** (not the raw PC name unless you want that) → team/role as needed.
5. Confirm GPS: Status shows a fix (Windows Location may need permission; NMEA optional).
6. On a map client (CloudTAK / ATAK / WinTAK), confirm your marker uses **your callsign**.
7. On TAK Server / Portal connection lists, confirm callsign matches Identity (not only the Windows computer name). Reconnect once after changing callsign if needed.

## What to file as Issues

Use [GitHub Issues](https://github.com/CopIXus/WinTAKTracker/issues) with:

- WinTAKTracker version (title bar)
- Setup vs portable
- Steps to reproduce
- Expected vs actual
- Screenshots OK if **redacted** (no real hosts, tokens, enroll URLs, certs, or unit callsigns you cannot share)

Good issue titles: `Enroll succeeds but SSL Test fails`, `Portal shows PC name instead of callsign`, `No GPS under Service mode`.

## Please do not

- Paste live enroll URLs, passwords, or `.p12` files into Issues or Slack
- Assume Portal “Connected Users” lists every SSL client the same way ATAK does — see [managed-devices.md](managed-devices.md)

## Docs

- [README](../README.md) — install and overview  
- [Windows Service](windows-service.md) — always-on / tray companion  
- [Managed devices](managed-devices.md) — Portal vs map vs TAK Server  
- [Remote config](remote-config.md) — Portal Send Configuration  
