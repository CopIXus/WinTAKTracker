# Security Policy

## Reporting a vulnerability

Please report security issues privately to the CopIXus maintainers (GitHub Security Advisories on the repository when available, or an org-listed contact). Do not open a public issue that includes exploit details until a fix is coordinated.

## Do not include operational TAK data

**Never** open issues, pull requests, discussions, or screenshots that contain:

- Real TAK server hostnames, IPs, or private CloudTAK URLs
- Enrollment URLs with tokens or credentials
- Usernames, passwords, API tokens, or callsigns tied to real users
- Client certificates, trust stores, SoftCert ZIPs, or private keys
- Unredacted logs or config exports from a live deployment

Use fictional placeholders (`tak.example.com`, `USER`, `TOKEN`) when illustrating a problem.

If you accidentally committed secrets to a fork or PR, rotate the credentials immediately and contact maintainers so the material can be purged from history where possible.

## Runtime secrets and ProgramData

| Mode | Config root | DPAPI |
|------|-------------|--------|
| Portable tray | `%LocalAppData%\WinTAKTracker\` | CurrentUser |
| Windows Service | `%ProgramData%\WinTAKTracker\` | LocalMachine |

Those directories must never be copied into this repository.

### Machine store ACLs (`%ProgramData%\WinTAKTracker`)

- **Root / `secrets/` / `certs/` / logs / updates:** `SYSTEM` + Administrators Full; Authenticated Users **Modify** (tray enroll/import writes certs and DPAPI secrets asInvoker; mutating IPC still requires an interactive client).

### Named-pipe IPC (`WinTAKTracker.Control`)

Mutating methods require an **interactive** Windows user (pipe client impersonation). When a settings lock password is configured and the service session is locked, `SetConfig` and identity mutators are rejected until `UnlockSettings`. Settings lock passwords are stored as SHA-256 + salt (legacy plaintext DPAPI blobs re-hash on successful unlock). Companion GPS pushes are limited to one active companion SID.

### TLS

`AllowInsecureTlsSoftAccept` defaults to **false**. Trust-store validation must succeed, or the connection is rejected. Soft-accept (SoftCert / private CA labs) is opt-in under Diagnostics.
