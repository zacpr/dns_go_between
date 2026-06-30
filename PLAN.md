# DNS Go-Between — Goal & Plan

## Goal

Build an ASP.NET Core solution that runs **on the DNS server**, uses local PowerShell cmdlets for DNS operations, provides a basic treeview web UI, and exposes REST endpoints for list/add/delete records. Keep a pluggable execution interface so v2 can switch to remote PSSession without API/UI changes.

## Architecture

```
C:\dns_go_between\
├── src\
│   ├── DnsGoBetween.Api            # ASP.NET Core Web API + Blazor Server UI
│   ├── DnsGoBetween.Core           # Domain models, validation, interfaces
│   └── DnsGoBetween.Infrastructure # PowerShell execution + DNS cmdlets adapter
└── tests\
    └── DnsGoBetween.Tests          # Unit tests for validation/service mapping
```

## Phases

### Phase 1 — Solution Bootstrap *(blocks all later steps)*
- Create the solution and project structure above.
- Add shared `appsettings.json` for allowed zones, allowed record types, audit options, and feature toggle for future remote provider.

### Phase 2 — DNS Service Abstraction & Local PowerShell Provider *(depends on 1)*
- Define `IDnsRecordService` with: `ListZones()`, `ListRecords(zone, node?)`, `AddRecord(request)`, `DeleteRecord(request)`.
- Define `IPowerShellDnsExecutor` abstraction:
  - `LocalPowerShellDnsExecutor` for v1.
  - Reserve `RemotePowerShellDnsExecutor` stub for v2 (not implemented).
- Wrap PowerShell cmdlets:
  - `Get-DnsServerZone`
  - `Get-DnsServerResourceRecord`
  - `Add-DnsServerResourceRecordA` / `...CName` / `...Ptr`
  - `Remove-DnsServerResourceRecord`
- Normalize PowerShell output into typed DTOs; map exceptions to structured API errors.

### Phase 3 — REST API Endpoints & AuthZ *(depends on 2)*
| Method | Route | Auth |
|--------|-------|------|
| GET | `/api/zones` | Operator (read) |
| GET | `/api/zones/{zone}/records` | Operator (read) |
| POST | `/api/records` | Admin (write) |
| DELETE | `/api/records` | Admin (write) |

- Strict request validation: zone allowlist, record-type allowlist, hostname/IP format, safe exact-match deletes.

### Phase 4 — Minimal Treeview Web UI *(parallel with 3 once contracts stable)*
- Single page: zone list panel, collapsible tree nodes per zone/hostname, record detail leaves.
- Admin-only add/delete controls with confirm dialog.
- UI calls API only — no direct cmdlet access.

### Phase 5 — Observability & Safety *(depends on 3, parallel with 4)*
- Audit log for all write attempts (user, action, target, outcome, correlation ID).
- Operation timeout + cancellation for PowerShell calls.
- Health endpoints: liveness + DNS cmdlet readiness check.

### Phase 6 — Packaging & Runbook *(depends on 1–5)*
- Host as Kestrel + Windows Service (preferred over IIS for simplicity).
- Deployment script for local environment setup.
- Operator runbook: startup, role assignment, common failures, rollback steps.

## Key Files (target paths)

| File | Purpose |
|------|---------|
| `src\DnsGoBetween.Api\Program.cs` | DI wiring, auth policies, endpoints, provider toggle |
| `src\DnsGoBetween.Api\Controllers\DnsController.cs` | REST handlers |
| `src\DnsGoBetween.Api\Pages\DnsTree.razor` | Treeview UI |
| `src\DnsGoBetween.Core\Interfaces\IDnsRecordService.cs` | App-facing DNS contract |
| `src\DnsGoBetween.Core\Interfaces\IPowerShellDnsExecutor.cs` | Execution transport contract |
| `src\DnsGoBetween.Infrastructure\Dns\LocalPowerShellDnsExecutor.cs` | Local cmdlet execution |
| `src\DnsGoBetween.Infrastructure\Dns\DnsRecordService.cs` | Service orchestration & mappings |
| `src\DnsGoBetween.Api\appsettings.json` | Zone/type allowlists and feature flags |
| `tests\DnsGoBetween.Tests\DnsRecordValidationTests.cs` | Request validation tests |

## Scope Decisions

| In v1 | Out of v1 (deferred to v2+) |
|-------|-----------------------------|
| Local-host execution only | Remote PSSession / JEA execution |
| List / Add / Delete API | Bulk import / export |
| Basic treeview UI | Record edit / update |
| Admin-only writes | Advanced filtering / search |

## v2 Migration Path

Introduce `RemotePowerShellDnsExecutor` and switch provider by config flag — no API or UI contract changes required.

## Acceptance Criteria

1. `GET /api/zones` returns expected zones when run on the DNS host.
2. Treeview expands large zones without full page reload.
3. Create test A record via API/UI; verify with `Resolve-DnsName`.
4. Delete same record via API/UI; verify removal.
5. Non-admin identity receives `403` on write endpoints.
6. Audit log entries exist for both success and failure writes.

## Current Project State (v1.0.11)

As of version 1.0.11, the project has transitioned from a proof-of-concept to a hardened, deployable tool.

### Implemented Features
- **PowerShell Adaption**: Robust parsing of Microsoft DNS cmdlets. Fixed critical bugs in record timestamp parsing (supporting varied local cultures/formats) and record deletion logic.
- **REST API**: Fully functional endpoints for Zones and Records. Supports A, AAAA, CNAME, and PTR types.
- **Web UI**: Blazor-based treeview with side-panel details and confirmation dialogs for destructive actions.
- **Authentication**: Dual-mode auth supporting both **Basic Auth** (configured via `appsettings.json`) and **Windows Authentication** (if hosted via IIS/Negotiate).
- **Deployment Automation**:
  - `build-installer.ps1`: Generates a WiX v4 MSI with custom file harvesting that handles symbolic links and cross-version (.NET Core vs 5.1) pathing correctly.
  - `redeploy.ps1`: A "one-touch" script that uninstalls, cleans stale files, builds, reinstalls, and verifies the whole stack.
- **Verification**: `scripts/smoke-test.ps1` provides automated API verification (GET/POST/DELETE) as part of the deployment pipeline.

### Hardened Infrastructure
- **Service Stability**: Implemented `Wait-For-Service` logic in deployment to prevent SCM race conditions during upgrades.
- **Error Handling**: Standardized on structured API responses for PowerShell failures, ensuring the UI doesn't crash on null properties or format errors.
- **Health Checks**: Detailed `/health/ready` check ensures not just that the service is up, but that the required `DnsServer` PowerShell module is responsive.

### Known Limitations
- Node.js/Playwright UI tests are scaffolded in `ui-tests/` but require a local Node environment to execute.
- API currently enforces exact-match deletion for safety.
- All records must belong to an explicitly allowed zone list in `appsettings.json`.

## Roadmap

### v1.3.9 (in flight)
- ICE03 / ICE61 WiX warning cleanup (beta.4, done).
- Installer upgrade UX: dialog pre-fills from previous install via persisted registry values (beta.5, done). Secrets excluded; blank PFX password = keep current.

### v1.4 — Windows CA (AD CS) Certificate Enrollment *(headline feature)*

Goal: let domain-joined deployments enroll their own TLS certificate from an internal Microsoft Enterprise CA instead of importing PFX / picking a pre-existing thumbprint. Runtime already speaks `STORE` source, so this is mostly installer work — ADCS is effectively "STORE that we provision ourselves."

**Scope phases:**

*v1.4 (target):*
- New `CERT_SOURCE=ADCS` option in `ConfigDlg`, with fields: template name (default `WebServer`), optional CA config string `CA-HOST\CA-Name`, optional extra SANs.
- New `scripts/enroll-cert.ps1` deferred CA: generates INF, runs `certreq -new`, `certreq -submit`, `certreq -accept`, captures resulting thumbprint, hands it to `update-config.ps1` as if user had picked `STORE` with that thumbprint.
- Synchronous (immediate-issue templates only); on any failure → fall back to self-signed and surface a banner in the admin UI.
- Manual **Re-enroll Now** button in the Settings page (calls the same script post-install).
- Documentation: required template ACLs, machine account "Enroll" permission, firewall (RPC dynamic ports for cert enrollment), troubleshooting matrix.

*v1.5 (deferred):*
- Background renewal worker inside `DnsGoBetween.Api` service: daily check, re-enroll at 75% of validity.
- Pending-approval polling (request ID persisted across service restarts).
- Admin UI: current cert expiry, next renewal time, enrollment history, alert thresholds.

*v1.6+ (deferred):*
- Non-domain enrollment with stored credentials (DPAPI machine-scope at minimum).
- Multiple CAs / per-zone templates.
- ACME (Let's Encrypt) source as a third enrollment path alongside ADCS.

**Open design questions:**
- Where to store enrollment state across installs? Suggested: `HKLM\Software\DnsGoBetween\Enrollment` (template name, last issued thumbprint, last attempt timestamp, last error) — fits the registry-persist pattern introduced in v1.3.9.
- Should `enroll-cert.ps1` be testable in CI? Likely needs an `ICertificateEnroller` C# abstraction with a fake implementation; PS script remains a thin install-time shim.
- Failure-mode UX: should a failed enrollment block the install, or silently fall back? Recommendation: fall back (install must succeed), show the error prominently in the UI after first launch.

**Effort estimate:** v1.4 happy path ~2-3 dev days. Add ~3-4 days for unit tests, docs, and the manual re-enroll UI. v1.5 renewal worker ~1 week.

