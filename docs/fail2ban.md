---
title: fail2ban and reconnect safety
---

# fail2ban (infra-TAK) and WinTAKTracker

[infra-TAK](https://github.com/takwerx/infra-TAK) can enable a **TAK Server** fail2ban jail that watches `/opt/tak/logs/takserver-messaging.log` for repeated **TLS handshake failures** (cert probes / bots).

## Jail behavior (infra-TAK docs)

| Setting | Typical value |
|---------|----------------|
| Trigger | Failed TLS handshakes on the streaming port (e.g. 8089) |
| Threshold | **~20 failures in 5 minutes** |
| Action | UFW ban (often ~1 hour; repeat offences can escalate) |

Related jails on the same hosts: `sshd`, `authentik`, `mediamtx-rtsp`. Operator IPs can be added to `ignoreip` in `/etc/fail2ban/jail.d/infratak-*.conf`.

A prior SAM outage looked like “the whole server is down” when fail2ban had **REJECT**’d the operator workstation IP after the `takserver` jail fired.

## How WinTAKTracker can trip it

SSL connect without a valid client cert (or with a rejected cert) still completes a TCP connect and a failed TLS handshake — the same log pattern the jail counts.

Aggressive **auto-reconnect** (especially older builds with reload races) can produce enough failed handshakes in five minutes to ban the client public IP.

## What WinTAKTracker does now

1. **Exponential backoff** on reconnect (longer delays for TLS/cert faults; capped ≤60 s for network).
2. **Circuit breaker (TLS/cert only)** — stop auto-reconnect after a small number of consecutive TLS/cert failures (well under 20/5 min). Sticky until the operator toggles **Connect**, changes host/certs, or uses **Test**.
3. **Network / unreachable failures keep retrying** — connect timeouts, DNS, and “no route” never permanently open the circuit. When connectivity returns (`NetworkChange` / availability), a debounced reload reconnects (including cancelling a mid-backoff wait so the device does not sit idle for up to a minute).
4. **Detailed Error** on the server card + Diagnostics log, including guidance to re-enroll / fix the cert and that fail2ban may be involved.
5. **Manual retry** for a TLS-suspended stream — toggle **Connect** off/on, or change host/certs, or use **Test**. Config saves do **not** clear a TLS circuit while suspended.

CoT streaming auth is **client certificate**, not username/password. Bad enroll credentials or a rejected `.p12` show up as TLS handshake failures and correctly trip the sticky circuit. Plain offline / cannot-reach-server must not.

## If you are banned

On the TAK host (via console SSH, Azure Run Command, etc.):

```bash
sudo fail2ban-client status takserver
sudo fail2ban-client set takserver unbanip YOUR.PUBLIC.IP
# also check: sshd authentik mediamtx-rtsp
sudo ufw status numbered   # remove REJECT for that IP if still present
```

Add trusted operator IPs to fail2ban `ignoreip` so lab testing cannot lock you out again.

## Not legal advice / not infra-TAK source

Thresholds are taken from infra-TAK public release notes (e.g. v10.1.11-alpha). Operators should confirm live jail settings on their box.
