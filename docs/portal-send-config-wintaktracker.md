# Portal: Send callsign / team to WinTAKTracker (ATAK-compatible)

**Audience:** TAK Portal / OpenTAK Portal developers  
**Goal:** Make **Send Configuration** (callsign, team color, role) work for **WinTAKTracker** the same way it already works for **ATAK**.  
**Product:** [CopIXus/WinTAKTracker](https://github.com/CopIXus/WinTAKTracker) — Windows tracking-only TAK PLI client (not a COP).

---

## Summary

WinTAKTracker already implements the **same Marti device-profile pull ATAK uses** after SSL connect:

`GET /Marti/api/device/profile/connection?clientUid={uid}`

If Portal only offers “Send Configuration” for `ATAK-CIV` / known Android clients, extend that UI and package targeting so **`takv.platform = WinTAKTracker`** clients receive the same preference package. No proprietary WinTAKTracker API is required for the ATAK-equivalent path.

---

## How ATAK receives Portal configuration today

Typical flow:

1. ATAK enrolls / connects with mTLS on CoT (e.g. `ssl:8089`).
2. ATAK requests device profile updates over Marti HTTPS (commonly **8443** / **8446**) with the **client certificate**.
3. Portal / TAK Server returns a **data package** (ZIP) or pref XML containing ATAK preference keys.
4. ATAK applies `locationCallsign`, team, role, etc.

Portal “Send Configuration” / “Send callsign to device” for ATAK ultimately lands in that **device profile / preference package** path (exact packaging may vary by Portal product).

---

## What WinTAKTracker already does (client side)

After a successful CoT SSL connect, WinTAKTracker best-effort:

1. Calls  
   `GET https://{host}:8443|8446/Marti/api/device/profile/connection?clientUid={DeviceUid}`  
   using the enrolled **client `.p12`** (mTLS), same idea as ATAK “Apply TAK Server Profile Updates”.
2. Parses ZIP / pref XML for identity keys (see below).
3. Applies callsign / team / role to the active Windows **user** identity when a tray session is present; otherwise **computer** identity.
4. Appends **`.wtt`** to remote callsigns (idempotent) so WinTAKTracker markers are distinct on the network (`HalavaALaptop.wtt`).
5. Operators can disable apply under **Identity → Apply callsign/team from Portal**.

Reference in-repo: [remote-config.md](remote-config.md), `DeviceProfileSync`, `PreferencePackageParser`, `RemoteIdentityApply`.

### Preference keys we read

Any of these (case-insensitive key match; ATAK-style names preferred):

| Purpose | Accepted keys |
|---------|----------------|
| Callsign | `locationCallsign`, `callsign` |
| Team color | `locationTeam`, `team`, `teamColor` |
| Role | `locationRole`, `role` |

Team values should be ATAK color **names**: `Cyan`, `Blue`, `Green`, `Yellow`, `Orange`, `Red`, `Purple`, …

### Client identity for targeting

| Field | WinTAKTracker value |
|-------|---------------------|
| CoT `takv@platform` | `WinTAKTracker` |
| CoT `takv@version` | e.g. `0.1.x` |
| CoT `event@uid` / profile `clientUid` | `WINDOWS-WinTAKTracker-…` (new installs) or legacy `WIN-…` |
| CoT `contact@callsign` | Operator / Portal-applied callsign (with `.wtt` when from Portal) |
| Enrollment username | Marti enroll user (cert CN / Portal username) — **not** the PLI callsign |

Use **`takv.platform == "WinTAKTracker"`** (and/or UID prefix `WINDOWS-WinTAKTracker-` / `WIN-`) when deciding which Connected Users rows can receive Send Configuration.

---

## What Portal should change

### 1. Treat WinTAKTracker as a configurable TAK client

In Connected Users / device management:

- Show rows where client type / `takv.platform` is **`WinTAKTracker`** (already appearing on many stacks once PLI is identified).
- Enable the same **Send Configuration** / callsign / team / role actions you enable for **ATAK-CIV**.

Do **not** require spoofing `ATAK-CIV` or `ANDROID-*` UIDs from the Windows client.

### 2. Deliver the same preference package ATAK gets

When an admin sends callsign/team/role to a WinTAKTracker device:

1. Build the **same** preference / device-profile artifact you build for ATAK (ZIP with `*.pref` / `config.pref`, or equivalent Marti device-profile payload).
2. Ensure that package is what Marti returns for  
   `GET /Marti/api/device/profile/connection?clientUid={that client's uid}`  
   (or the Portal-specific queue that feeds that ATAK path).
3. Include at least:

```text
locationCallsign=<callsign without requiring .wtt>
locationTeam=<Cyan|Blue|…>
locationRole=<Team Member|…>   (optional)
```

WinTAKTracker will append `.wtt` itself if missing.

### 3. Match on the correct client UID

ATAK uses `ANDROID-…`. WinTAKTracker uses `WINDOWS-WinTAKTracker-…` or `WIN-…`.

When queuing a profile for a connected user, key the package by the **CoT UID / clientUid** shown for that TLS session (same column you already show for ATAK), not by username alone if multiple devices share one enroll user.

### 4. Optional: preference / SoftCert URL (secondary)

WinTAKTracker also accepts paste/import of:

- `tak://…/preference?locationCallsign=…&locationTeam=…`
- SoftCert ZIP containing `config.pref`

Useful for testing without Connected Users UI, but **device-profile-on-connect** is the ATAK-parity path for Portal Send Configuration.

---

## What WinTAKTracker will not do

- It does **not** install full mission content, plugins, or arbitrary data packages beyond identity prefs.
- Inbound CoT on the streaming socket is not used as a primary config channel (tracking-only).
- It will not pretend to be ATAK for list filtering; Portal should whitelist `WinTAKTracker` explicitly.

---

## Suggested acceptance test

1. Enroll WinTAKTracker with a Portal enroll URL; confirm **Connected** and PLI on CloudTAK/ATAK map.
2. Confirm TAK Server / Portal shows client type **WinTAKTracker** and a stable UID (`WINDOWS-…` or `WIN-…`).
3. From Portal, **Send Configuration** with callsign `TestUnit` and team `Cyan` to that device (same control as ATAK).
4. Reconnect WinTAKTracker (or wait for next profile sync after connect).
5. Expect Identity / outbound PLI callsign **`TestUnit.wtt`**, team **Cyan**.
6. Map clients show `TestUnit.wtt`.

If step 3 is disabled in UI for non-ATAK clients, that is the Portal gap — the Windows client already pulls the Marti profile URL.

---

## Contact / references

| Resource | Link |
|----------|------|
| Repository | https://github.com/CopIXus/WinTAKTracker |
| Client remote-config notes | https://github.com/CopIXus/WinTAKTracker/blob/main/docs/remote-config.md |
| Managed device / presence notes | https://github.com/CopIXus/WinTAKTracker/blob/main/docs/managed-devices.md |
| Issues | https://github.com/CopIXus/WinTAKTracker/issues |

Please redact live enroll tokens, hostnames, and certificates in any shared logs or screenshots.
