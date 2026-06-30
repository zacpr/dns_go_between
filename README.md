# DNS Go-Between

DNS Go-Between is a Windows-hosted DNS management service that exposes DNS operations through a secure REST API and Blazor UI.

It runs on a Windows host as a service, ships as an MSI, and can manage DNS on the **local Windows DNS Server** out of the box. Additional providers (remote Windows DNS via PowerShell remoting, Cloudflare, AWS Route 53, Namecheap) can be enabled by adding their credentials to `appsettings.json`.

## What it does

- Manages DNS zones and records across one or more providers (Windows DNS, remote Windows DNS, Cloudflare, AWS Route 53, Namecheap)
- Adds and deletes supported records (`A`, `AAAA`, `CNAME`, `PTR`, `TXT`)
- Provides a browser UI for operators with Settings, Audit History, and Health views
- Exposes provider-aware REST endpoints for automation/scripts
- Persists a configurable audit history of every write operation
- Enforces authz, zone/type controls, per-provider write roles, and optional IP access controls

## Automated certificate issuance (ACME DNS-01)

DNS Go-Between ships with ready-made **PowerShell hooks** in the `acme/` directory that let any ACME client obtain and renew TLS certificates via DNS-01 validation — no inbound HTTP port required.

Because DNS-01 works by publishing a `_acme-challenge` TXT record, DNS Go-Between's REST API is the automation layer: the hooks call `POST /api/records` to create the challenge and `DELETE /api/records` to clean up.

### Why this matters

- Works for **wildcard certificates** (`*.example.com`) — the only ACME challenge type that does
- Works from **air-gapped or internally-facing servers** with no public HTTP(S) port
- No ACME client needs direct access to the DNS server — it only needs HTTPS to the API
- Supports Certbot, win-acme, acme.sh, or any tool that supports a script/hook model

### Quick start

```powershell
$env:DNSGOBET_URL  = "https://your-dns-server:6790"
$env:DNSGOBET_USER = "dnsadmin"
$env:DNSGOBET_PASS = "s3cret"

# Certbot
certbot certonly --manual --preferred-challenges dns `
    --manual-auth-hook    "pwsh -File acme/dns-challenge-create.ps1" `
    --manual-cleanup-hook "pwsh -File acme/dns-challenge-delete.ps1" `
    -d example.com -d "*.example.com"
```

See [`acme/README.md`](acme/README.md) for full examples including win-acme, acme.sh, and troubleshooting.

---

## Resource requirements

Typical footprint on a small-to-medium DNS server:

- Memory (working set): ~150 MB to 350 MB under normal usage
- Recommended RAM budget: 512 MB reserved for the service
- High activity bursts (large record queries/multiple users): up to ~500 MB

These are practical estimates, not hard limits, and will vary with zone size, query volume, and concurrent UI/API usage.

## Security defaults

DNS Go-Between is HTTPS-first by default.

- Default listener: `https://0.0.0.0:6790`
- HTTP is disabled by default (`Tls:EnableHttp=false`)
- Installer prompts for allowed zones, HTTPS port, and certificate source
- If both `ipwhitelist.txt` and `ipblacklist.txt` contain active rules, whitelist takes precedence
- `/health/live` and `/health/ready` are restricted to loopback callers only

Additional hardening in current builds:

- Structured audit logging for write operations (add/delete) with correlation IDs
- Persistent audit history (`audit_history.json`) with configurable retention
- Sanitized API error payloads (no raw stack traces or internal command output)
- Startup diagnostics logs include listener mode, TLS source, and redirect settings
- Forwarded headers (`X-Forwarded-For` / `X-Forwarded-Proto`) honored from loopback proxies

## Authentication and authorization

- Authentication:
  - Windows Negotiate (Kerberos/NTLM)
  - HTTP Basic (for non-domain/proxied clients)
  - Basic auth can be disabled (`Auth:EnableBasicAuthentication=false`)
  - Basic auth can require TLS (`Auth:RequireHttpsForBasicAuthentication=true`)
  - Basic auth supports per-client lockout controls:
    - `Auth:BasicAuthenticationFailureLimit`
    - `Auth:BasicAuthenticationLockoutSeconds`
- Authorization:
  - Read access: authenticated users
  - Write access (`POST` / `DELETE`): members of the configured write roles
    - Default roles: `DnsAdmins`, `Domain Admins`
    - Override globally with `Dns:DefaultWriteRoles` (array)
    - Override per provider with `Dns:Providers:<ProviderName>:WriteRoles` (array)

## TLS certificate selection

At startup, certificate resolution is:

1. External PFX (`Tls:Certificate:PfxPath` + `PfxPassword`)
2. Explicit store selection (`StoreName`, `StoreLocation`, `Thumbprint` or `Subject`)
3. Automatic machine cert discovery from LocalMachine\My based on host/FQDN (if `Tls:AutoSelectMachineCertificate=true`)
4. If no certificate is available, primary listener falls back to HTTP on the configured primary port to preserve reachability

### Example TLS config

```json
"Tls": {
  "HttpsPort": 6790,
  "EnableHttp": false,
  "HttpPort": 0,
  "RedirectHttpToHttps": false,
  "AutoSelectMachineCertificate": true,
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

### Core DNS settings

```json
"Dns": {
  "AllowedZones": [],
  "AllowedRecordTypes": ["A", "AAAA", "CNAME", "PTR", "TXT"],
  "CommandTimeoutSeconds": 30,
  "DefaultWriteRoles": ["DnsAdmins", "Domain Admins"]
}
```

### DNS providers

The local Windows DNS provider (`Windows`) is always registered. Additional providers are enabled only when their credentials are present in configuration:

```json
"Dns": {
  // Remote Windows DNS via PowerShell remoting (WSMan)
  "RemoteWindowsServer":   "dns-other.contoso.local",
  "RemoteWindowsUser":     "CONTOSO\\dnsadmin",
  "RemoteWindowsPassword": "...",

  // Cloudflare — token form (preferred)
  "CloudflareApiToken": "cf-token-...",
  // — or legacy email + global key form
  "CloudflareEmail":  "you@example.com",
  "CloudflareApiKey": "...",

  // AWS Route 53
  "AwsAccessKey": "AKIA...",
  "AwsSecretKey": "...",
  "AwsRegion":    "us-east-1",

  // Namecheap
  "NamecheapApiUser": "user",
  "NamecheapApiKey":  "...",

  // Optional: per-provider write role overrides
  "Providers": {
    "Cloudflare": { "WriteRoles": ["CloudDnsOps"] },
    "Route53":    { "WriteRoles": ["CloudDnsOps"] }
  }
}
```

The set of active providers is visible at runtime under **Settings** in the UI and via `GET /api/providers`.

### Audit history

Write operations are appended to `audit_history.json` in the install directory and surfaced in the **History** page of the UI.

```json
"Audit": {
  "HistoryRetentionDays": 30
}
```

Restart service after config edits:

```powershell
Restart-Service DnsGoBetween
```

## API overview

All endpoints are provider-aware. If no `{provider}` segment is supplied, requests default to the local `Windows` provider.

- `GET    /api/providers`
- `GET    /api/zones`
- `GET    /api/{provider}/zones`
- `GET    /api/zones/{zone}/records`
- `GET    /api/{provider}/zones/{zone}/records`
- `POST   /api/records`
- `POST   /api/{provider}/records`
- `DELETE /api/records`
- `DELETE /api/{provider}/records`

Write endpoints require the caller to be a member of one of the write roles for the targeted provider (see [Authentication and authorization](#authentication-and-authorization)). The `POST` / `DELETE` endpoints support TXT records, which makes the API suitable as an ACME DNS-01 backend — see [Automated certificate issuance](#automated-certificate-issuance-acme-dns-01) above.

### Admin UI pages

- `/`              — DNS tree (browse/edit zones and records)
- `/settings`      — active providers, allowlists, auth/TLS settings (write-role gated)
- `/history`       — audit history of write operations (write-role gated)
- `/health-status` — on-demand health check results (write-role gated)

Swagger:

- `/swagger` (available in all environments; endpoints still require auth)

Health:

- `/health/live`
- `/health/ready`

Note: health endpoints are intentionally loopback-only (not remotely reachable). When the configured cloud providers are enabled, their reachability is also reflected in `/health/ready`.

## Build and test

```powershell
# API build
TEMP=/tmp TMP=/tmp dotnet build src/DnsGoBetween.Api/DnsGoBetween.Api.csproj -c Release

# Unit tests
dotnet test tests/DnsGoBetween.Tests

# MSI build (Windows)
./scripts/build-installer.ps1 -Version 1.3.8 -Configuration Release
```

## Release process

Tag with `v*` and push to trigger release workflow:

```bash
git tag -a v1.3.8 -m "Release v1.3.8"
git push origin v1.3.8
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
