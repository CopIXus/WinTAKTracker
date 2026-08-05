# Remote configuration (Portal “Send Configuration”)

WinTAKTracker can apply **callsign** and **team color** pushed from a TAK Portal / OpenTAK-style admin flow. It does **not** build a COP or install arbitrary mission content — only identity prefs used for self-SA PLI.

## What gets applied

| Field | Behavior |
|-------|----------|
| Callsign | Set on the active **user** identity when a Windows session is known; otherwise **computer** identity. Always ends with **`.wtt`** (appended if missing; not doubled). |
| Team | ATAK team color **name** (`Cyan`, `Blue`, …) → CoT `__group@name`. |
| Role | Optional; applied when present. |

Internal API: `TrackingHost.ApplyRemoteIdentity(callsign, team, role)` and `RemoteIdentityApply` in Core.

## Receive paths (implemented)

1. **Device profile on connect** (ATAK-compatible path)  
   After a successful CoT stream connect, WinTAKTracker best-effort `GET`s  
   `/Marti/api/device/profile/connection?clientUid=…` on HTTPS ports **8443** / **8446** using the profile client certificate (same idea as ATAK “Apply TAK Server Profile Updates”).  
   If the response is a data-package ZIP (or pref XML), prefs are scanned for identity keys (below).

2. **Portal Pref mission package (fileshare CoT)**  
   When Portal uses Marti `missioncreate` / Enterprise Sync to push  
   `Pref-{Callsign}-{Team}-{Role}.zip` (`MANIFEST/manifest.xml` + `certs/config.pref`) to this client UID,  
   WinTAKTracker detects the inbound fileshare CoT, downloads via  
   `GET /Marti/sync/content?hash=…` (mTLS), and auto-imports when `onReceiveImport=true`.

3. **Enrollment / preference URLs**  
   Paste `tak://…/preference?locationCallsign=…&locationTeam=…` (or SoftCert / Pref ZIP) under **Settings → Servers**. Same `.wtt` + team rules apply.

4. **Manual Pref / SoftCert ZIP import**  
   Identity-only Pref packages (no `.p12`) and SoftCert packages with `config.pref` both apply through the same helpers.

### Preference keys we read

| Purpose | Keys |
|---------|------|
| Callsign | `locationCallsign`, `callsign` |
| Team | `locationTeam`, `team`, `teamColor` (incl. `Dark Green`, `Dark Blue`, `Brown`) |
| Role | `atakRoleType`, `locationRole`, `role` |

## What is still limited

- Full Portal wire formats vary by server (OpenTAK Server, TAK Server, custom portals). Empty or non-pref profile responses are ignored; failures are logged without breaking PLI.
- Arbitrary mission content (maps, overlays, plugins) is not installed — only callsign / team / role from Pref packages.
- Do not put real hosts, tokens, or live enroll URLs in this repository — see [SECURITY.md](../SECURITY.md).

## Operator tip

Portal **Send Callsign Preferences** can update a live session via Pref ZIP fileshare (no reconnect required). Device-profile pull still runs after connect. Callsigns appear on the network as `NAME.wtt`.

## For Portal developers

See [portal-send-config-wintaktracker.md](portal-send-config-wintaktracker.md) — how to enable Send Configuration for `takv.platform=WinTAKTracker` using the same Marti device-profile path as ATAK.
