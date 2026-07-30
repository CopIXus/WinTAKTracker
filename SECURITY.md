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

## Runtime secrets

WinTAKTracker stores enrollment material under `%LocalAppData%\WinTAKTracker\` with DPAPI protection for secret blobs. That directory must never be copied into this repository.
