# DNS Go-Between

A lightweight REST API and Blazor web UI that runs **directly on a Windows DNS Server** and exposes your DNS zones and records over HTTP. It bridges the gap between the Windows DNS PowerShell module and any tool (curl, scripts, or your browser) that needs to read or manage DNS without RDP or direct PowerShell access.

---

## Features

- **Web UI** — collapsible zone/host/record tree at `http://<server>:6790/`
- **REST API** — JSON endpoints for listing zones, listing records, adding records, and deleting records
- **Swagger UI** — interactive API docs at `/swagger`
- **Windows Service** — runs under the `LocalSystem` account, starts automatically on boot
- **Dual authentication** — Windows Negotiate (Kerberos/NTLM) for domain clients; HTTP Basic for non-domain callers
- **Role-based write control** — only members of `DnsAdmins` or `Domain Admins` can add/delete records
- **Zone & record-type allowlists** — restrict which zones and record types are exposed via `appsettings.json`
- **Audit logging** — every write attempt is logged with user, action, target, outcome, and correlation ID
- **Health endpoints** — liveness and DNS-cmdlet readiness checks at `/health/live` and `/health/ready`

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows Server with DNS Server role | The service calls `DnsServer` PowerShell cmdlets locally |
| .NET 8 runtime | Included in the MSI installer |
| Port 6790 open on the server firewall | The installer adds the firewall rule automatically |
| Run installer as Administrator | Required for service registration and `C:\Program Files` write |

---

## Installation

### Recommended — MSI installer

1. Download `DnsGoBetween-<version>.msi` from the [latest release](https://github.com/zacpr/dns_go_between/releases/latest).
2. On the DNS server, open PowerShell **as Administrator** and run:

   ```powershell
   msiexec /i "DnsGoBetween-<version>.msi" /qb /l*vx "$env:TEMP\dns_install.log"
   ```

   Or double-click the MSI for the interactive setup wizard.

3. Verify the service started:

   ```powershell
   Get-Service DnsGoBetween
   Invoke-RestMethod http://localhost:6790/health/live
   Invoke-RestMethod http://localhost:6790/health/ready
   ```

Both health endpoints should return `200 OK`. If `ready` returns `503`, the service cannot reach the DNS PowerShell module yet — check the Windows Application event log.

### Silent / unattended install

```powershell
msiexec /i "DnsGoBetween-<version>.msi" /qn /norestart
```

### Uninstall

```powershell
msiexec /x "DnsGoBetween-<version>.msi" /qb
# or via Add / Remove Programs in Windows Settings
```

---

## Configuration

The service reads `C:\Program Files\DnsGoBetween\appsettings.json` on startup.

```json
{
  "Dns": {
    "AllowedZones": [],
    "AllowedRecordTypes": ["A", "AAAA", "CNAME", "PTR"],
    "CommandTimeoutSeconds": 30
  },
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:6790" }
    }
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `AllowedZones` | `[]` (all) | Restrict API to specific zones. Empty means all zones are exposed. |
| `AllowedRecordTypes` | `["A","AAAA","CNAME","PTR"]` | Record types permitted for read and write operations. |
| `CommandTimeoutSeconds` | `30` | Timeout for each PowerShell DNS cmdlet call. |

After editing, restart the service:

```powershell
Restart-Service DnsGoBetween
```

---

## Usage

### Web UI

Open `http://<dns-server>:6790/` in a browser. You will be prompted for Windows credentials.

- **Browse** — click a zone to expand it; click a hostname to see its records
- **Add** — members of `DnsAdmins` or `Domain Admins` see an **Add record** button per zone/host
- **Delete** — same admin roles see a delete (✕) button on each record

### REST API

All endpoints require authentication. Use Windows Negotiate or HTTP Basic credentials.

#### List zones

```http
GET /api/zones
```

```json
[
  { "name": "example.com", "zoneType": "Primary" },
  { "name": "10.in-addr.arpa", "zoneType": "Primary" }
]
```

#### List records in a zone

```http
GET /api/zones/{zone}/records
GET /api/zones/example.com/records?node=webserver
```

```json
[
  { "hostName": "webserver", "zoneName": "example.com", "recordType": "A", "data": "10.0.0.5", "timeToLive": 3600 }
]
```

#### Add a record

Requires `DnsAdmins` or `Domain Admins`.

```http
POST /api/records
Content-Type: application/json

{
  "zoneName": "example.com",
  "hostName": "webserver",
  "recordType": "A",
  "data": "10.0.0.5",
  "timeToLive": 3600
}
```

Returns `201 Created` on success.

#### Delete a record

Requires `DnsAdmins` or `Domain Admins`. All fields must match exactly.

```http
DELETE /api/records
Content-Type: application/json

{
  "zoneName": "example.com",
  "hostName": "webserver",
  "recordType": "A",
  "data": "10.0.0.5"
}
```

Returns `204 No Content` on success.

#### Swagger / interactive docs

```
http://<dns-server>:6790/swagger
```

### Example with curl

```bash
# List zones (Windows auth from a domain machine)
curl --negotiate -u : http://dns-server:6790/api/zones

# List zones (Basic auth)
curl -u "DOMAIN\\username:password" http://dns-server:6790/api/zones

# Add an A record
curl -u "DOMAIN\\admin:password" \
     -X POST http://dns-server:6790/api/records \
     -H "Content-Type: application/json" \
     -d '{"zoneName":"example.com","hostName":"test","recordType":"A","data":"10.0.0.99","timeToLive":300}'
```

---

## Authentication

| Client type | Method | Notes |
|---|---|---|
| Domain-joined Windows machine | Windows Negotiate (Kerberos/NTLM) | No credentials needed; browser/curl use the current session |
| Non-domain or Linux/Mac clients | HTTP Basic | Pass `DOMAIN\username:password` as Basic credentials |

Write operations (`POST`, `DELETE`) additionally require the caller to be a member of the **DnsAdmins** or **Domain Admins** AD group.

---

## Building from source

```powershell
# 1. Clone the repo
git clone https://github.com/zacpr/dns_go_between.git
cd dns_go_between

# 2. Build and run the API locally (development mode)
dotnet run --project src/DnsGoBetween.Api

# 3. Run unit tests
dotnet test tests/DnsGoBetween.Tests

# 4. Build the MSI installer (requires Windows + .NET 8 SDK)
.\scripts\build-installer.ps1 -Version 1.0.1
# Output: dist\DnsGoBetween-1.0.1.msi
```

### Releasing

Tag the commit with a `v`-prefixed semantic version and push. GitHub Actions builds the MSI and publishes it as a GitHub Release automatically:

```bash
git tag -a v1.2.0 -m "Release v1.2.0"
git push origin v1.2.0
```

You can also trigger a release manually from [Actions → Release On Tag → Run workflow](https://github.com/zacpr/dns_go_between/actions/workflows/release-on-tag.yml).

---

## Project structure

```
src/
  DnsGoBetween.Api/           # ASP.NET Core Web API + Blazor Server UI
  DnsGoBetween.Core/          # Domain models, interfaces, validation
  DnsGoBetween.Infrastructure/ # PowerShell DNS executor + audit logger
tests/
  DnsGoBetween.Tests/         # Unit tests
installer/
  DnsGoBetween.Installer/     # WiX v4 MSI project
scripts/
  build-installer.ps1         # Build API + MSI
  redeploy.ps1                # Re-deploy in-place on the server
  uninstall.ps1               # Clean uninstall helper
```

---

## Troubleshooting

**Service installed but won't start**
Check the Windows Application event log for `DnsGoBetween.Api`. The most common cause is a Kestrel HTTPS endpoint configured without a certificate. The installer normalises `appsettings.json` to HTTP-only — if you edited it, ensure only the HTTP endpoint is present.

**`/health/ready` returns 503**
The service cannot invoke `Get-DnsServerZone` via Windows PowerShell 5.1. Ensure the DNS Server role is installed and the service account has permission to run DNS cmdlets. Check the event log for `DnsCommandHealthCheck` errors.

**Error 1625 during install**
Local Group Policy is blocking MSI installs. Run the installer as a Domain Admin, or ask your GPO admin to permit it.

**Port 6790 unreachable from other machines**
The installer creates a Windows Firewall inbound rule for TCP 6790. Verify it exists:

```powershell
Get-NetFirewallRule -DisplayName "DNS Go-Between HTTP"
```
