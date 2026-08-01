# Managed devices (TAK Server / Portal)

WinTAKTracker connects like ATAK: **mTLS on SSL:8089**, then **self-SA CoT (PLI)** that carries callsign, team, role, `takv`, and a stable device UID.

## What each UI shows

| UI | Source of truth | WinTAKTracker |
|----|-----------------|---------------|
| CloudTAK / ATAK map | CoT PLI (`contact@callsign`) | Shows callsign when PLI is sent |
| TAK Server connections | TLS session, then **first PLI** binds callsign/UID/platform | Shows `tls:N` until PLI; then callsign + `WINDOWS-WinTAKTracker-…` UID |
| Portal **Connected Users** | Portal / CloudTAK **sessions** (web + enrolled ATAK-style clients) | Often **omits** raw SSL trackers unless Portal ingests SA for all clients |

CloudTAK using UID `ANDROID-CloudTAK-{user}` and ATAK using `ANDROID-…` does **not** mean WinTAKTracker must spoof Android. Spoofing `ATAK-CIV` / `ANDROID-*` would mis-label managed devices.

## What we send (client)

On each TAK connect:

1. Presence PLI shortly after connect (waits briefly for tray user session so the first label is **your callsign**, not the Windows computer name)
2. Re-announce when the interactive user session binds a different callsign
3. Self-SA shaped like ATAK: `contact@callsign` + `endpoint=*:-1:stcp`, `uid@Droid`, `__group`, `takv platform=WinTAKTracker`, `precisionlocation`
4. Ping reply: server `t-x-c-t` → client `t-x-c-t-r`
5. Device UID: `WINDOWS-WinTAKTracker-{machineGuid}` (existing `WIN-*` UIDs are left unchanged)

**Do not** put a bare hostname in `remarks` — some Portal UIs have treated that text as the callsign. Optional “Computer: {name}” remarks are off by default (Reporting settings).

## Portal gap

If Portal’s Connected Users list still only shows CloudTAK / ATAK after the above, that list is **Portal product behavior**, not a missing WinTAKTracker TLS handshake. Options on the Portal/server side:

- Treat TAK Server identified SSL clients (callsign + `takv`) as managed users, or
- Add an explicit device-registry API that trackers can call

WinTAKTracker already pulls Portal “Send Configuration” via Marti device-profile sync; that path is **not** the same as presence registration.
