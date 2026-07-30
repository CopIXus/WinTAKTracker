# Contributing to WinTAKTracker

Thanks for helping improve this Windows TAK PLI tracker.

## Before you open a PR

- [ ] No real TAK hosts, IPs, enroll URLs, tokens, passwords, or callsigns
- [ ] No `.p12` / `.pfx` / `.pem` / SoftCert ZIPs or other cert material
- [ ] Screenshots and logs are redacted (fictional `tak.example.com` only)
- [ ] Samples stay under `samples/` with obvious fake values
- [ ] `dotnet build` succeeds on Windows with .NET 8 SDK
- [ ] You did not commit `%LocalAppData%\WinTAKTracker\` files or `local/` scratch

## Development

```powershell
dotnet build WinTAKTracker.sln -c Debug
dotnet run --project src/WinTAKTracker
```

Publish (self-contained single file):

```powershell
dotnet publish src/WinTAKTracker -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Code style

- Match existing project structure under `src/WinTAKTracker/`
- Prefer small, focused changes; stubs/interfaces are fine ahead of later phases
- Do not invent real operational server names in tests or docs

## License

Contributions are accepted under the Apache License 2.0 (see `LICENSE`).
