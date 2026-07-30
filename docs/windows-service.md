# Always-on tracking (Windows Service)

**Status:** implemented (vertical slice) — service + Core + IPC + computer/user identity.  
**Goal:** install once → PLI continues whenever the PC is powered on, including after interactive **user logoff**.

---

## Product model

| Mode | When | Behavior |
|------|------|----------|
| **Windows Service** | `WinTAKTracker` SCM service installed and running | Tracking runs in Session 0 from `%ProgramData%\WinTAKTracker\`. Tray UI is a **controller** (named pipe IPC) — it does **not** start a second tracker. |
| **Portable / in-process** | Service not installed or not reachable | Tray app owns GPS + TAK + Mesh (legacy). Config under `%LocalAppData%\WinTAKTracker\`. |

### Identity rules

1. **Computer callsign** (`computerIdentity`) — used when nobody is logged on / no user identity is active. Default = `Environment.MachineName` until set. Optional computer team/role/CoT type.
2. **Per-user callsign** (`userIdentities[sid]`) — when a Windows user is logged in and has a saved callsign, CoT uses that user’s callsign/team/role.
3. **First login prompt** — if the current Windows user has no saved callsign, the tray asks once. Skip → fall back to computer callsign (`setupPromptDismissed`).
4. **On logoff** — service reverts CoT identity to computer callsign (session watcher + tray clears session over IPC).

Legacy `identity` in `config.json` is mirrored from `computerIdentity` for compatibility.

---

## Architecture

```
[Boot]
   └─ WinTAKTracker.Service  (SCM, LocalSystem)
         ├─ WinTAKTracker.Core (GPS, TAK, Mesh, Reporting)
         ├─ Config: %ProgramData%\WinTAKTracker\
         ├─ Secrets: DPAPI LocalMachine under ProgramData\secrets\
         └─ Named pipe: WinTAKTracker.Control

[User logon]
   └─ WinTAKTracker.exe (tray)
         └─ Attach to pipe → status, settings, pause, identity
```

| Project | Role |
|---------|------|
| `WinTAKTracker.Core` | UI-free library: GPS, CoT, TAK, Mesh, config, enrollment, IPC protocol, `TrackingHost` |
| `WinTAKTracker.Service` | .NET Worker + Windows Service host |
| `WinTAKTracker` | WPF tray / Settings companion (or portable tracker) |

---

## Install / run

### Build

```powershell
dotnet build WinTAKTracker.sln -c Release
dotnet publish src/WinTAKTracker.Service -c Release -r win-x64 --self-contained true -o publish/service
dotnet publish src/WinTAKTracker -c Release -r win-x64 --self-contained true -o publish/tray
```

### Install service (elevated)

```powershell
# From repo root, after publish:
powershell -ExecutionPolicy Bypass -File scripts\install-service.ps1 -MigrateUserConfig
```

Manual equivalent:

```powershell
sc.exe create WinTAKTracker binPath= "C:\Program Files\WinTAKTracker\WinTAKTracker.Service.exe" start= auto
sc.exe failure WinTAKTracker reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start WinTAKTracker
```

Uninstall: `scripts\install-service.ps1 -Uninstall` or `sc.exe stop/delete WinTAKTracker`.

### Run without installing (dev)

```powershell
dotnet run --project src/WinTAKTracker.Service
# separate terminal — tray attaches if pipe is up:
dotnet run --project src/WinTAKTracker
```

---

## Config & secrets

| Store | Path | DPAPI |
|-------|------|--------|
| Portable tray | `%LocalAppData%\WinTAKTracker\` | CurrentUser |
| Service | `%ProgramData%\WinTAKTracker\` | LocalMachine |

**Migration:** On first service start, if ProgramData has no `config.json` but LocalAppData does, the service copies config/certs and attempts to re-protect secrets. CurrentUser DPAPI blobs **cannot** be decrypted as LocalSystem — re-enter tokens/cert passwords after install if migration could not re-protect them. Prefer running `install-service.ps1 -MigrateUserConfig` while logged on as the enrolled user, then re-save secrets from Settings (writes LocalMachine blobs via the service).

**Never** commit real hosts, tokens, or certs. Use fakes only (`tak.example.com`, `USER`, `TOKEN`, `CALLSIGN`).

---

## GPS viability (logged off / Session 0)

| Source | Always-on service |
|--------|-------------------|
| NMEA serial | Expected to work (COM ACLs for service account) |
| IP geolocation | Expected to work (coarse) |
| Windows Location (WinRT) | Best-effort / often unavailable as LocalSystem — do not rely on it after logoff |

---

## IPC control plane

Named pipe **`WinTAKTracker.Control`** (ACL: Users read/write, Administrators + SYSTEM full). JSON line protocol methods include: `Ping`, `GetStatus`, `GetConfig`, `SetConfig`, `Pause`, `Resume`, `ReloadConnections`, `SetComputerIdentity`, `SetUserIdentity`, `SetActiveSession`, `DismissUserSetupPrompt`.

---

## Deferred / Phase 3

- Service-aware updater (stop service → replace binaries → start)
- Dedicated service account (least privilege) instead of LocalSystem
- WiX / MSIX installer UI
- Optional companion bridge for Windows Location fixes over IPC while logged on
- Refined Settings chrome beyond computer vs my callsign fields

---

## Threat note

Machine-wide secrets (LocalMachine DPAPI / ProgramData ACLs) are readable by local Administrators. Stronger than CurrentUser against other interactive users on the same box after logoff; weaker against admin/malware-as-admin. Always-on PLI after logoff may need org policy consent.
