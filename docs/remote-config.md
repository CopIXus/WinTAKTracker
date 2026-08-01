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

1. **Device profile on connect** (primary ATAK-compatible path)  
   After a successful CoT stream connect, WinTAKTracker best-effort `GET`s  
   `/Marti/api/device/profile/connection?clientUid=…` on HTTPS ports **8443** / **8446** using the profile client certificate (same idea as ATAK “Apply TAK Server Profile Updates”).  
   If the response is a data-package ZIP (or pref XML), `*.pref` / config prefs are scanned for `locationCallsign` / `callsign`, `locationTeam` / `team` / `teamColor`, and `locationRole` / `role`.

2. **Enrollment / preference URLs**  
   Paste `tak://…/preference?locationCallsign=…&locationTeam=…` (or SoftCert ZIP with `config.pref`) under **Settings → Servers**. Same `.wtt` + team rules apply.

3. **SoftCert / import URL**  
   Prefs inside SoftCert packages are applied through the same helper.

## What is still limited

- Full Portal wire formats vary by server (OpenTAK Server, TAK Server, custom portals). Empty or non-pref profile responses are ignored; failures are logged without breaking PLI.
- Inbound CoT SA / file-share mission packages on the streaming socket are still drained, not fully parsed for config (tracking-only). Prefer device-profile packages or preference URLs.
- Do not put real hosts, tokens, or live enroll URLs in this repository — see [SECURITY.md](../SECURITY.md).

## Operator tip

After changing callsign/team in Portal, reconnect the client (or restart the WinTAKTracker service/tray) so profile sync runs again. Callsigns appear on the network as `NAME.wtt`.

## For Portal developers

See [portal-send-config-wintaktracker.md](portal-send-config-wintaktracker.md) — how to enable Send Configuration for `takv.platform=WinTAKTracker` using the same Marti device-profile path as ATAK.
