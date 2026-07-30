# Always-on tracking (Windows Service design)

**Status:** design only — not implemented.  
**Goal:** install once → PLI continues whenever the PC is powered on, including after interactive **user logoff**.

Today WinTAKTracker is a **WPF + NotifyIcon tray app** that lives entirely in an interactive user session. “Start with Windows” only restarts it when that user logs on again. True logoff persistence requires a different process model.

---

## 1. How tightly coupled is the current app?

| Component | Session coupling | Notes |
|-----------|------------------|--------|
| `App.xaml` / `App.xaml.cs` | **Hard** | WPF `Application`, `MessageBox` on duplicate instance / crash |
| `TrayIconService` | **Hard** | WinForms `NotifyIcon`; balloons; settings window; `Application.Current.Shutdown` on Quit |
| `AppHost` | **Medium** | Composes GPS + TAK + Mesh + Reporting + Tray + Updates; always constructs tray |
| `StartupRegistration` | **Hard (user)** | HKCU `Run` only — dies with logoff, never Session 0 |
| `PowerService` | **Session-ish** | `SetThreadExecutionState` on the calling thread; optional prevent-sleep |
| `DpapiProtector` / secrets | **Hard (user)** | `DataProtectionScope.CurrentUser` under `%LocalAppData%\WinTAKTracker\` |
| `WindowsLocationGps` | **Hard** | WinRT `Geolocator`; `RequestAccessAsync` routed to WPF UI dispatcher for consent |
| `NmeaSerialGps` | **Low** | COM port I/O; works headless if the service account can open the port |
| `NetworkIpGeolocationGps` | **Low** | HTTPS outbound; fine for SYSTEM if network exists |
| `TakConnectionManager` / TLS | **Low** | TCP/TLS + cert files; no UI dependency |
| `MeshSaBroadcaster` | **Low–medium** | UDP multicast + NIC selection; works headless; NIC list/VPN heuristics unchanged |
| `UpdateService` | **Medium** | Quits EXE and swaps file; incompatible with a running SCM-hosted service without stop/replace |
| `SingleInstanceMutex` | **Session-local** | `Local\…` mutex — per session; would not coordinate with a service |

**Verdict:** tracking *logic* (CoT fan-out, Mesh, NMEA, IP geo) can run headless. Tray, settings UI, Windows Location consent, CurrentUser DPAPI, HKCU Run, and self-update-via-quit are session-bound and must be redesigned for always-on.

---

## 2. Windows constraints (honest)

### Tray / UI
- Services run in **Session 0**. There is **no** per-user tray shell there. `NotifyIcon` cannot appear for a logged-off (or never-logged-on) user from a service process.
- Settings and balloons must live in a **companion** process that starts at user logon and talks to the service.

### GPS
- **NMEA serial:** usually viable as a service if the COM device is present and ACLs allow the service account. Prefer this for true always-on PLI.
- **Windows Location (WinRT Geolocator):** designed for interactive apps; permission UX and provider behavior often assume a user session. Treat as **unreliable or unavailable after logoff** / as LocalSystem. Do not depend on it for the always-on path.
- **IP geolocation:** works without a user session (coarse); acceptable fallback only.

### Secrets / DPAPI
- Blobs protected with **CurrentUser** decrypt only for that Windows user. After logoff, a service running as LocalSystem or another account **cannot** read today’s `%LocalAppData%\…\*.dpapi` secrets.
- Migration to **LocalMachine** DPAPI, or an ACL’d encrypted store under ProgramData, is mandatory for service-held TAK tokens/passwords. Client certs (`.p12`/PEM on disk) need the same ACL story.

### Mesh / network
- Mesh SA multicast and TAK TCP/TLS generally work from Session 0.
- NIC selection still matters (VPN vs LAN). Auto-pick heuristics in `MeshSaBroadcaster` remain valid; validate on target hardware while logged off.

### Install / trust
- Creating a Windows Service requires **elevation** (Administrator) once.
- Unsigned service EXEs face the same SmartScreen / Smart App Control friction as the tray app ([code signing](code-signing.md)).

### What “Start with Windows” loses today
On logoff: process ends → no CoT, no Mesh, no GPS polling. On next logon: HKCU Run may restart the app for that user only. Multi-user / fast-user-switch: only the logged-on user’s instance (if any) tracks.

---

## 3. Architecture options

### A) Windows Service + tray companion (**recommended**)

```
[Boot]
   └─ WinTAKTracker.Service  (SCM, LocalSystem or dedicated account)
         ├─ GPS (NMEA + optional IP; Windows Location opt-in / degraded)
         ├─ TAK TLS/TCP + Mesh SA
         ├─ Reporting engine
         └─ Named pipe / local RPC control plane

[User logon]
   └─ WinTAKTracker.exe (tray UI only)
         └─ Attach to pipe → status, settings, pause, enroll UI
```

| Pros | Cons |
|------|------|
| True logoff / reboot persistence | Secrets model + ACLs redesign |
| Clear separation of headless vs UI | Installer / elevation required |
| Matches industry “agent + tray” pattern | Updates must stop service, replace, start |
| Mesh + TAK + NMEA viable | Windows Location may stay user-session-only |

### B) Assigned Access / auto-logon kiosk

Keep a user session always logged on (kiosk / auto-logon). Tracking keeps working because a session never ends.

| Pros | Cons |
|------|------|
| Minimal code change | **Not** real logoff persistence |
| Tray still works | Security surface (auto-logon password) |
| | Wrong fit for shared / domain PCs |

### C) Task Scheduler “At startup” / “Whether user is logged on or not”

Run the existing (or headless) EXE as a scheduled task with stored credentials.

| Pros | Cons |
|------|------|
| No full SCM project at first | Still credential-bound; password rotation pain |
| Can start at boot | Not a clean service lifecycle; UI still wrong in Session 0 |
| | DPAPI-CU still breaks if task user ≠ enroll user |

Often a stepping stone, not the destination.

### D) Keep user-session autostart only (**current**)

| Pros | Cons |
|------|------|
| Already shipped | Stops on logoff |
| Simplest security (user-scoped secrets) | Not “PC is on ⇒ tracking” |

---

## 4. Recommendation for WinTAKTracker

**Build option A:** split into:

1. **`WinTAKTracker.Core`** (class library) — GPS orchestration, TAK, Mesh, reporting, config I/O, logging (no WPF/WinForms).
2. **`WinTAKTracker.Service`** — `BackgroundService` / `ServiceBase` host; no tray; starts at boot via SCM.
3. **`WinTAKTracker`** (existing tray) — becomes a **client**: status, settings, enrollment, pause; optional “Start tray with Windows” (HKCU) separate from “Run tracking service”.

**Service account:** prefer a dedicated local service account with least privilege over blanket LocalSystem long-term; LocalSystem is acceptable for an early MVP if ProgramData ACLs and cert handling are tight.

**GPS policy for always-on:** require **NMEA (or IP fallback)** for guaranteed logged-off PLI; document Windows Location as “when a user is logged on / companion may bridge” unless proven under the service identity.

**Do not** pursue B as the product answer. Use C only as a temporary ops workaround.

---

## 5. Required work (phased)

### Phase 0 — Product contract (small)
- Settings: distinguish **Start tray with Windows** vs **Run as Windows Service (always-on)**.
- Document GPS viability matrix (NMEA / Windows Location / IP) for logged-off mode.
- Threat note: machine-wide secrets vs CurrentUser (see §6).

### Phase 1 — Extract headless core
- Move non-UI services from `AppHost` into `WinTAKTracker.Core`.
- Introduce `ITrackingHost` that does not construct `TrayIconService`.
- Config root dual-mode: `%LocalAppData%` (legacy user) vs `%ProgramData%\WinTAKTracker\` (service).
- Secrets: `DataProtectionScope.LocalMachine` (or AES key in ACL’d file) for service store; one-time migrate-from-CU tool run elevated while user is logged on.

### Phase 2 — Service host + IPC
- `WinTAKTracker.Service` registered with SCM (`sc.exe`, WiX, or custom elevated installer).
- Control plane: **named pipe** with ACL limited to Administrators + interactive users (or a specific group), JSON/MessagePack RPC:
  - get status (GPS, servers, mesh, pause)
  - apply config / reload
  - pause / resume
  - enroll / import cert (UI gathers secrets → service writes machine store)
- Headless: no WPF in service process; no `MessageBox`; no `Application.Current`.

### Phase 3 — Tray as companion
- Tray detects service; if present, attach instead of owning GPS/TAK.
- If service absent, keep today’s in-process mode for portable / no-admin users (**dual mode**).
- Single-instance: Global mutex for service; Local mutex for tray; avoid double-reporting.

### Phase 4 — Install / update / privilege
- Elevated install: copy service EXE, `sc create` / WiX `ServiceInstall`, set recovery (restart on failure), start Automatic.
- Updates: stop service → replace binaries → start service; tray helper must not only “wait for PID and relaunch EXE”.
- Uninstall: stop/delete service, optional wipe ProgramData (prompt).

### Phase 5 — Hardening
- Explicit Mesh NIC when Auto is ambiguous at boot (adapters may come up late — retry bind).
- COM port permissions for the service account.
- Optional: deny Windows Location in service; companion can publish fix over IPC only while logged on (advanced).

**Effort (rough):** Phase 1–2 ≈ medium multi-week; Phase 3–4 ≈ similar; Phase 5 polish. Not a small scaffolding PR.

---

## 6. Out of scope / risks

| Risk | Why it matters |
|------|----------------|
| TAK certs / tokens as SYSTEM or machine-wide | Any admin (or malware-as-admin) can read LocalMachine DPAPI / ACL’d files; stronger than CU for offline attackers on that box |
| Multi-user machines | One always-on identity vs per-user profiles — product must pick “device tracker” semantics |
| Enrollment UX while logged off | Portal/QR/camera enrollment stays in the companion; service only stores results |
| Smart App Control / unsigned service | Install may be blocked; signing remains required for smooth deploy |
| Prevent-sleep as service | Need service-appropriate power requests; AwayMode semantics differ |
| Legal / policy | Always-on PLI after logoff may need org consent; not a code issue |

**Never** commit real TAK hosts, tokens, or certs into this repo. Runtime only under LocalAppData / ProgramData.

---

## 7. What breaks vs today (summary)

| Feature | After logoff (service path) |
|---------|-----------------------------|
| Tray icon / balloons | Gone until user logs on (companion) |
| Settings / QR enroll | Companion only |
| Windows Location | Likely broken / unsupported |
| DPAPI CurrentUser secrets | Must migrate |
| NMEA serial | Expected to work |
| IP fallback | Expected to work |
| TAK TLS + Mesh SA | Expected to work |
| HKCU “Start with Windows” | Insufficient alone |
| In-place EXE self-update | Must become service-aware |

---

## 8. What to build next

1. **Land this design** (this doc) and decide dual-mode (portable tray vs always-on service).
2. **Extract `WinTAKTracker.Core`** with no WPF references — biggest enabler, valuable even before SCM.
3. **Spike:** minimal Worker service that opens NMEA (or mocks GPS), sends one Mesh/CoT path, proves boot + logoff.
4. **Secrets migration spike:** CU → LocalMachine under ProgramData with ACL.
5. **IPC stub** (named pipe status) + tray “connected to service” indicator.

Optional tiny stubs in-repo can wait until Phase 1 starts; prefer not to add empty service projects until Core extraction begins.
