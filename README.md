# DNS Go-Between

DNS Go-Between is a Windows-hosted DNS management service that exposes Windows DNS Server operations through a secure REST API and Blazor UI.

It runs on the DNS server, uses local PowerShell DNS cmdlets, and is packaged as an MSI with Windows Service hosting.

## What it does

- Lists zones and records from Windows DNS
- Adds and deletes supported records (`A`, `AAAA`, `CNAME`, `PTR`)
- Provides a browser UI for operators
- Exposes API endpoints for automation/scripts
- Enforces authz, zone/type controls, and optional IP access controls

## Security defaults

DNS Go-Between is HTTPS-first by default.

- Default listener: `https://0.0.0.0:6790`
- HTTP is disabled by default (`Tls:EnableHttp=false`)
- Installer prompts for HTTPS port
- If both `ipwhitelist.txt` and `ipblacklist.txt` contain active rules, whitelist takes precedence

## Authentication and authorization

- Authentication:
  - Windows Negotiate (Kerberos/NTLM)
  - HTTP Basic (for non-domain/proxied clients)
- Authorization:
  - Read access: authenticated users
  - Write access (`POST` / `DELETE`): `DnsAdmins` or `Domain Admins`

## TLS certificate selection

At startup, certificate resolution is:

1. External PFX (`Tls:Certificate:PfxPath` + `PfxPassword`)
2. Explicit store selection (`StoreName`, `StoreLocation`, `Thumbprint` or `Subject`)
3. Automatic machine cert discovery from LocalMachine\My based on host/FQDN
4. If none are found, Kestrel default HTTPS certificate handling applies

### Example TLS config

```json
"Tls": {
  "HttpsPort": 6790,
  "EnableHttp": false,
  "HttpPort": 0,
  "Certificate": {
    "StoreName": "My",
    "StoreLocation": "LocalMachine",
    "Thumbprint": "",
    "Subject": "",
    "PfxPath": "",
    "PfxPassword": ""
  }
}
```

## IP whitelist / blacklist

Use files in the install directory:

- `ipwhitelist.txt`
- `ipblacklist.txt`

Format:

- Comma/semicolon/newline-separated entries
- Each entry can be a single IP or CIDR
- `#` comments are supported

Rules:

- Use one list at a time.
- If whitelist has active rules, only matching clients are allowed.
- If both lists are active, whitelist takes precedence.
- If whitelist is empty/inactive, blacklist rules are applied.

## Installation (MSI)

1. Download MSI from releases.
2. Install as Administrator:

```powershell
msiexec /i "DnsGoBetween-<version>.msi" /qb /l*vx "$env:TEMP\dns_install.log"
```

3. Verify service and health:

```powershell
Get-Service DnsGoBetween
Invoke-RestMethod https://localhost:6790/health/live -SkipCertificateCheck
Invoke-RestMethod https://localhost:6790/health/ready -SkipCertificateCheck
```

### Maintenance after install

Use Start Menu shortcut:

- `DNS Go-Between -> Modify DNS Go-Between`

This reopens MSI maintenance so you can adjust:

- Allowed zones
- HTTPS port

## Configuration

Primary config file:

- `C:\Program Files\DnsGoBetween\appsettings.json`

Core DNS settings:

```json
"Dns": {
  "AllowedZones": [],
  "AllowedRecordTypes": ["A", "AAAA", "CNAME", "PTR"],
  "CommandTimeoutSeconds": 30
}
```

Restart service after config edits:

```powershell
Restart-Service DnsGoBetween
```

## API overview

- `GET /api/zones`
- `GET /api/zones/{zone}/records`
- `POST /api/records`
- `DELETE /api/records`

Swagger (Development environment):

- `/swagger`

Health:

- `/health/live`
- `/health/ready`

## Build and test

```powershell
# API build
TEMP=/tmp TMP=/tmp dotnet build src/DnsGoBetween.Api/DnsGoBetween.Api.csproj -c Release

# Unit tests
dotnet test tests/DnsGoBetween.Tests

# MSI build (Windows)
./scripts/build-installer.ps1 -Version 1.0.2 -Configuration Release
```

## Release process

Tag with `v*` and push to trigger release workflow:

```bash
git tag -a v1.0.2 -m "Release v1.0.2"
git push origin v1.0.2
```

The release workflow builds a Windows MSI and attaches it to the GitHub Release.

## Troubleshooting

- HTTPS startup issues:
  - Ensure certificate exists and is accessible to the service account.
  - Check Windows Application event log for `DnsGoBetween.Api`.
- `health/ready` failing:
  - Verify DNS role/cmdlets availability and service permissions.
- Install blocked (1625):
  - Check local/domain MSI policy restrictions.
- Remote access blocked unexpectedly:
  - Review active entries in `ipwhitelist.txt` and `ipblacklist.txt`.
