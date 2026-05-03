# DNS Go-Between Final Setup & Testing Runbook

## Phase 1: Build Fixed MSI

Build the latest installer:

```powershell
cd c:\dns_go_between
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
.\scripts\build-installer.ps1 -Version 1.0.10
```

Expected result:

```text
Build complete!
MSI  : C:\dns_go_between\dist\DnsGoBetween-1.0.6.msi
MSI  : C:\dns_go_between\dist\DnsGoBetween-1.0.10.msi
```

## Phase 2: Install MSI (Elevated Required)

Run PowerShell as Administrator:

```powershell
$msi = "c:\dns_go_between\dist\DnsGoBetween-1.0.10.msi"
$log = "$env:TEMP\dns_install_1010.log"
msiexec /i "$msi" /qb /l*vx "$log"
Restart-Service DnsGoBetween
Get-Content "C:\Program Files\DnsGoBetween\appsettings.json"
Invoke-WebRequest "http://localhost:6790/health/live"
Invoke-WebRequest "http://vdc01.ashurtech.net:6790/health/live"
```

Optional: install with explicit service identity (new MSI properties):

```powershell
# Built-in LocalService (no password)
msiexec /i "$msi" /qb SERVICE_ACCOUNT="NT AUTHORITY\LocalService" /l*vx "$log"

# Domain user account
msiexec /i "$msi" /qb SERVICE_ACCOUNT="ASHURTECH\svc-dns-gobetween" SERVICE_PASSWORD="<secret>" /l*vx "$log"

# gMSA (password omitted; include trailing $)
msiexec /i "$msi" /qb SERVICE_ACCOUNT="ASHURTECH\svc-dns-gobetween$" /l*vx "$log"
```

Notes:

- Non-admin installs may fail with MSI error 1625 due to local policy.
- Use the log file above for troubleshooting if install fails.

## Phase 3: Start Service and Verify

```powershell
Start-Service DnsGoBetween
Get-Service DnsGoBetween | Format-Table Name, Status, StartType
Invoke-WebRequest "http://localhost:6790/health/live"
Invoke-WebRequest "http://localhost:6790/health/ready"
```

Expected:

- Service status is Running.
- Health endpoint returns HTTP 200.
- Liveness endpoint (`/health/live`) returns HTTP 200.
- Readiness endpoint (`/health/ready`) returns HTTP 200 when DNS cmdlets are accessible to the running service.

## Phase 4: API Check

Swagger URL:

```text
http://localhost:6790/swagger/index.html
```

## Troubleshooting

### Service installs but does not start

Root cause seen previously:

- Installed `appsettings.json` had Kestrel HTTPS endpoint (`https://localhost:6791`) with no cert for LocalSystem.
- Service crashed with `Unable to configure HTTPS endpoint`.

Current fix in build pipeline:

- `scripts/build-installer.ps1` now normalizes publish `appsettings.json` to HTTP-only before WiX harvest and binds HTTP to `0.0.0.0:6790`.
- `LocalPowerShellDnsExecutor` now executes DNS cmdlets through Windows PowerShell 5.1 for DNS module compatibility.
- Readiness failures are logged by `DnsCommandHealthCheck` for easier diagnosis.

If needed, quick in-place fix (elevated):

```powershell
Copy-Item "c:\dns_go_between\src\DnsGoBetween.Api\appsettings.json" "C:\Program Files\DnsGoBetween\appsettings.json" -Force
Start-Service DnsGoBetween
```

### Build fails with installer obj permission denied

Current fix in build pipeline:

- `scripts/build-installer.ps1` passes `MSBuildProjectExtensionsPath` for installer build to temp (`%TEMP%`) to avoid locked `installer\...\obj` writes.

### dotnet not found

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
```

## Key Files

- [scripts/build-installer.ps1](scripts/build-installer.ps1)
- [installer/DnsGoBetween.Installer/Service.wxs](installer/DnsGoBetween.Installer/Service.wxs)
- [installer/DnsGoBetween.Installer/HarvestedFiles.wxs](installer/DnsGoBetween.Installer/HarvestedFiles.wxs)
- [src/DnsGoBetween.Api/appsettings.json](src/DnsGoBetween.Api/appsettings.json)

## Current Status

- Build pipeline fixed and stable on this machine.
- Installer and service startup are confirmed working.
- `/health/live` is confirmed healthy under the installed service.
- Latest fixed MSI generated successfully: `dist\DnsGoBetween-1.0.10.msi`.
- Remaining validation is remote reachability under the updated `1.0.10` install.
