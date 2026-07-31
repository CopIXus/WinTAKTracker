---
title: Code signing & SmartScreen
---

# Code signing, SmartScreen, and Smart App Control

WinTAKTracker ships as a downloaded `WinTAKTracker.exe`. On many Windows 11 systems that EXE is blocked or warned about until it carries a trusted **Authenticode** signature and builds publisher reputation. This page explains why, what end users can do today, and how CopIXus enables signing in release CI.

**Status:** Release CI signs the EXE **only when repository secrets are configured**. Until then, GitHub Releases remain unsigned. Do not assume a download is signed unless you verify the signature locally (see [Verify a signature](#verify-a-signature)).

[← Home](index.md) · [Features](features.md) · [Changelog](changelog.md) · [Download latest](https://github.com/CopIXus/WinTAKTracker/releases/latest)

## Why Windows blocks unsigned downloads

| Layer | What it does |
|-------|----------------|
| **SmartScreen** | Reputation filter for apps and downloads. Unknown or rarely seen publishers get “Windows protected your PC.” Users can often continue via **More info → Run anyway**. |
| **Smart App Control (SAC)** | Stricter Windows 11 mode (especially in **Enforcement**). Unsigned or poorly reputated apps from the internet may be **blocked** with no “Run anyway.” SAC is stricter than classic SmartScreen. |

Neither layer is defeated by a self-signed certificate for public downloads. Windows expects a certificate from a trusted commercial CA (or Microsoft Artifact Signing / Trusted Signing), plus enough positive reputation over time.

## Temporary workarounds (end users)

Use these only if you trust the source (official [GitHub Releases](https://github.com/CopIXus/WinTAKTracker/releases) for this repo) and have verified the SHA256 sidecar when possible.

1. **SmartScreen warning** — In the blue/yellow dialog: **More info** → **Run anyway**.
2. **Mark of the Web / Unblock** — Right-click `WinTAKTracker.exe` → **Properties** → if present, check **Unblock** → **OK**, then run again.
3. **Smart App Control** — Settings → Privacy & security → Windows Security → App & browser control → Smart App Control:
   - If still in **Evaluation**, you can turn SAC **Off** (cannot re-enable Evaluation later without reinstall/reset in many cases).
   - In **Enforcement**, unsigned downloads are often hard-blocked; turning SAC off or obtaining a properly signed build is required.
4. **Build from source** — Clone the repo and `dotnet publish` locally (see [README](https://github.com/CopIXus/WinTAKTracker#build-from-source)). Locally built binaries are not “internet downloads” in the same way, but org policy may still apply.

These workarounds are **not** a substitute for Authenticode signing for general distribution.

## Proper fix: Authenticode code signing

1. Obtain a code-signing identity issued to **CopIX LLC** (Azure Trusted Signing / Artifact Signing certificate profile, or EV/OV from a commercial CA). The Authenticode **publisher display name** should read **CopIX LLC** when the certificate subject matches that legal entity — assembly/Inno metadata alone cannot fake a trusted signature.
2. In release CI, sign `WinTAKTracker.exe` after publish (requires Azure Trusted Signing or PFX secrets; see below). Without those secrets, Releases stay **unsigned**.
3. Always attach a **timestamp** (RFC 3161) so the signature remains valid after the cert expires.
4. Verify with `signtool` or `Get-AuthenticodeSignature` before publishing (`Status = Valid`, publisher **CopIX LLC**).
5. Over time, consistent signed releases from the same publisher improve SmartScreen / SAC reputation. Signing alone (especially OV) may **not** instantly silence warnings.

### Certificate options (tradeoffs)

| Option | Pros | Cons | SAC / SmartScreen notes |
|--------|------|------|-------------------------|
| **Azure Artifact Signing** (formerly **Trusted Signing**) | Cloud HSM, CI-friendly, Microsoft integration, no USB token on the build agent | Azure subscription + identity verification; usage-based cost; setup of account + certificate profile | Often the best first choice for GitHub Actions; reputation still builds with distribution volume |
| **EV code signing** | Strongest traditional SmartScreen reputation path; hardware-backed keys | Higher cost; USB token or cloud HSM; slower procurement | Best “instant trust” profile among classic certs; still not magic on day one for brand-new publishers |
| **OV / standard code signing** | Lower cost than EV; signs the binary correctly | Soft keys increasingly restricted; SmartScreen may warn until reputation accumulates | Signs ≠ silent install; expect warnings early on |
| **Self-signed** | Free for local experiments | Not trusted by Windows for public downloads | **Does not** fix SAC/SmartScreen for Releases |

Commercial certificates require a verifiable **legal organization** (registered business, address, contacts). Personal/individual certs (where still offered) have different limits; plan on org validation for CopIX LLC.

### Rough process

```text
Obtain cert / Azure Artifact Signing profile
        ↓
Publish WinTAKTracker.exe (CI)
        ↓
Sign + timestamp (Azure action or signtool)
        ↓
Recompute SHA256 sidecar
        ↓
Publish GitHub Release
        ↓
Verify: signtool verify / Get-AuthenticodeSignature
```

Public timestamp examples:

- Microsoft (Artifact Signing default): `http://timestamp.acs.microsoft.com`
- DigiCert: `http://timestamp.digicert.com`
- Sectigo: `http://timestamp.sectigo.com`

## CI in this repository

Workflow: [`.github/workflows/release.yml`](https://github.com/CopIXus/WinTAKTracker/blob/main/.github/workflows/release.yml).

Signing runs **only when secrets exist**:

1. **Preferred:** Azure Artifact Signing (Trusted Signing) via `azure/artifact-signing-action` when Azure + Trusted Signing secrets are set.
2. **Fallback:** Classic `signtool` with a PFX from `CODE_SIGN_PFX_BASE64` + `CODE_SIGN_PFX_PASSWORD`.

If neither set is present, the workflow publishes an **unsigned** EXE (current OSS contributor path). No certificates or passwords are stored in git.

### GitHub secrets checklist

#### Azure Artifact Signing / Trusted Signing (preferred)

| Secret | Purpose |
|--------|---------|
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_CLIENT_ID` | App registration (service principal) client ID |
| `AZURE_CLIENT_SECRET` | Client secret for that app (or use OIDC later; see Azure docs) |
| `AZURE_TRUSTED_SIGNING_ENDPOINT` | Regional endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_TRUSTED_SIGNING_ACCOUNT` | Artifact / Trusted Signing account name |
| `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE` | Certificate profile name |

Azure setup sketch:

1. Create an Azure subscription and complete Artifact Signing (Trusted Signing) account + certificate profile (public trust).
2. Create an Entra app registration / service principal with role **Artifact Signing Certificate Profile Signer** (or equivalent) on the signing account.
3. Add the secrets above to **GitHub → Settings → Secrets and variables → Actions**.
4. Push to `main` (or cut a `v*` tag) and confirm the release job logs show signing succeeded.
5. On a Windows PC: `Get-AuthenticodeSignature .\WinTAKTracker.exe` → `Status` should be `Valid`.

If identity validation is still **Pending** or no certificate profile exists yet, CI will **attempt** Trusted Signing, warn on failure, and still publish **unsigned** Setup/EXE so releases are not blocked. Remove the Azure signing secrets (or finish validation + create a Public Trust profile) once you want hard-fail signing again.

Optional hardening: switch from client secret to **OIDC federated credentials** (`azure/login` + `id-token: write`) so no long-lived secret is stored in GitHub. See [Azure artifact-signing-action OIDC docs](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md).

#### Classic PFX (alternative)

| Secret | Purpose |
|--------|---------|
| `CODE_SIGN_PFX_BASE64` | Base64-encoded `.pfx` (never commit the PFX file) |
| `CODE_SIGN_PFX_PASSWORD` | PFX password |

Encode locally (do not echo into logs or tickets):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\secure\path\codesign.pfx")) | Set-Clipboard
```

CI uses DigiCert’s public timestamp URL with `signtool`. Prefer EV/OV certs stored in hardware or a vault; treat the GitHub secret as highly sensitive and rotate if exposed.

## Verify a signature

```powershell
Get-AuthenticodeSignature .\WinTAKTracker.exe | Format-List *

# Or Windows SDK:
signtool verify /pa /v .\WinTAKTracker.exe
```

Expected when signing is enabled and healthy: `Status = Valid`, publisher matching your org cert, and a timestamp present.

Also compare the SHA256 file from the Release:

```powershell
Get-FileHash .\WinTAKTracker.exe -Algorithm SHA256
Get-Content .\WinTAKTracker.exe.sha256
```

## Recommendation for CopIX LLC

**Start with Azure Artifact Signing (Trusted Signing):** it fits GitHub Actions, avoids shipping a PFX to runners, and aligns with Microsoft’s SmartScreen reputation path for cloud-signed apps. Complete org identity validation under **CopIX LLC**, create one certificate profile used only for WinTAKTracker releases, and wire the secrets listed above. Product/company metadata in the EXE and Setup already says **CopIX LLC**; Authenticode still requires a real cert issued to that entity — there is no way to “sign” from the repo without those secrets.

Consider a classic **EV** certificate later if you need maximum traditional SmartScreen trust for offline/enterprise distribution or if Azure signing is unavailable in your region/subscription. Prefer **not** to rely on OV alone if the goal is quiet installs for new users—plan for a reputation ramp either way.

Self-signed certs are fine for internal experiments only; they will not fix SAC/SmartScreen for public Releases.
