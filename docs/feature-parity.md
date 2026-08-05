# Feature parity — WinTAKTracker vs AndroidTAKTracker

Shared feature IDs keep the sibling repos aligned. Status values: **Yes**, **Partial**, **Planned**, **N/A**.

Sibling: [AndroidTAKTracker](https://github.com/CopIXus/AndroidTAKTracker)

| Feature ID | Description | WinTAKTracker | AndroidTAKTracker |
|---|---|---|---|
| FP-TAK-TLS | TAK Server TLS/mTLS CoT stream + fail2ban guard | Yes | Yes |
| FP-REPORTING-ASAP | Dynamic/Constant reporting + ASAP on motion/identity | Yes | Yes |
| FP-GPS-FUSED | Platform fused / Windows Location provider | Yes | Yes |
| FP-GPS-IP-FALLBACK | IP geolocation (ipwho.is) delayed fallback | Yes | Yes |
| FP-MESH-SA | UDP Mesh SA multicast 239.2.3.1:6969 | Yes | Yes |
| FP-ENROLL-QR | QR / deep-link enrollment; Marti CSR + SoftCert PKCS12 persist (`atakatak`) | Yes | Yes |
| FP-MDM-HEADWIND | Headwind MDM + Android Enterprise managed config | N/A | Yes |
| FP-PORTAL-CALLSIGN | Device-profile sync + Pref-*.zip fileshare; `.wtt` / `.att` | Yes (.wtt) | Yes (.att) |
| FP-ATAK-DEFER | Suppress PLI when ATAK is active | N/A | Yes |
| FP-BOOT-START | Start tracking after boot / login | Yes | Yes |
| FP-UPDATES-CHANGELOG | GitHub Releases + inline CHANGELOG notes | Yes | Yes |
| FP-SETTINGS-LOCK | Settings lock password | Yes | Yes |
| FP-PAUSE | Pause outbound CoT without quitting | Yes | Yes |
| FP-DIAGNOSTICS | Log level, TLS soft-accept, status export; Android in-app log viewer/share | Yes (folder) | Yes (viewer) |
| FP-VIDEO | In-app video push / CoT advertise | Yes | N/A — ICU VideoStreamer companion |

When adding a feature, update this table in **both** repos and note the sibling in the PR.
