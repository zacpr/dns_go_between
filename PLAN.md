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

### v1.3.9 (shipped)
- Multi-provider DNS abstraction (Cloudflare, AWS Route 53, Namecheap, Remote Windows) with 28 new unit tests.
- ICE03 / ICE61 WiX warning cleanup.
- Installer upgrade UX: dialog pre-fills from previous install via persisted registry values; secrets excluded; blank PFX password = keep current.
- Swagger: rich `OpenApiInfo` description + XML doc comments wired through `IncludeXmlComments`.

### v1.4 — Cert Station: AD CS Enrollment + ACME DNS-01 *(headline)*

Two complementary cert-issuance paths shipping together so DNS Go-Between becomes a one-stop cert provisioning point for both internal (AD CS) and public (Let's Encrypt / ZeroSSL / any ACME CA) certificates.

#### v1.4a — Windows CA (AD CS) certificate enrollment

Goal: let domain-joined deployments enroll their own TLS certificate from an internal Microsoft Enterprise CA instead of importing PFX / picking a pre-existing thumbprint. Runtime already speaks `STORE` source, so this is mostly installer work — ADCS is effectively "STORE that we provision ourselves."

- New `CERT_SOURCE=ADCS` option in `ConfigDlg`, with fields: template name (default `WebServer`), optional CA config string `CA-HOST\CA-Name`, optional extra SANs.
- New `scripts/enroll-cert.ps1` deferred CA: generates INF, runs `certreq -new`, `certreq -submit`, `certreq -accept`, captures resulting thumbprint, hands it to `update-config.ps1` as if user had picked `STORE` with that thumbprint.
- Synchronous (immediate-issue templates only); on any failure → fall back to self-signed and surface a banner in the admin UI.
- Manual **Re-enroll Now** button in the Settings page (calls the same script post-install).
- Documentation: required template ACLs, machine account "Enroll" permission, firewall (RPC dynamic ports for cert enrollment), troubleshooting matrix.

#### v1.4b — ACME DNS-01 sink

Goal: since DNS Go-Between already owns DNS write access to multiple providers, exposing a built-in ACME DNS-01 challenge solver turns it into a self-contained Let's Encrypt issuer. No external `lego` / `acme.sh` needed; the same provider abstractions that serve admin record management also satisfy `_acme-challenge` TXT publication.

- Embed an ACME v2 client (`Certes` or roll-your-own thin client) inside `DnsGoBetween.Api`.
- New endpoint `POST /api/certificates/request` taking `{commonName, subjectAltNames[], provider, contactEmail}`. Returns an order ID for polling.
- DNS-01 solver writes `_acme-challenge.<name>` TXT records via the existing `IDnsProvider` matching the apex zone; verifies propagation; instructs ACME to validate.
- On issuance, install cert into `LocalMachine\My`, return thumbprint, optionally swap as the service's own TLS cert if `--use-as-server-cert` flag set.
- Background renewal worker (shared with v1.4a renewal logic in v1.5).
- New admin page `Certificates.razor`: list issued certs, expiry, last renewal, manual renew/revoke buttons.

#### v1.4 — Open design questions
- Where to store enrollment state across installs? Suggested: `HKLM\Software\DnsGoBetween\Enrollment` (last issued thumbprint, last attempt timestamp, last error) — fits the registry-persist pattern introduced in v1.3.9.
- Should `enroll-cert.ps1` be testable in CI? Likely needs an `ICertificateEnroller` C# abstraction with a fake implementation; PS script remains a thin install-time shim.
- ACME account key storage: DPAPI machine-scope file under `%ProgramData%\DnsGoBetween\acme\account.key`. Backup/restore in the admin runbook.
- Failure-mode UX: should a failed enrollment block the install, or silently fall back? Recommendation: fall back (install must succeed), show the error prominently in the UI after first launch.

**Effort estimate:** v1.4a happy path ~2-3 dev days. v1.4b ~3-4 days (ACME client integration is the biggest unknown). Add ~3-4 days combined for unit tests, docs, and the manual re-enroll/issue UI.

### v1.5 — Provider Expansion + Cert Renewal Worker

#### Tier-1 DNS providers
- **Azure DNS** — natural fit with the Windows / AD CS direction. SDK: `Azure.ResourceManager.Dns`. Entra ID auth (managed identity when running on Azure VMs, service principal otherwise). Hybrid customers (Windows on-prem + Azure DNS for cloud zones) are the sweet spot.
- **Google Cloud DNS** — completes the big-3 hyperscalers alongside AWS Route 53. SDK: `Google.Cloud.Dns.V1`. Service account JSON credential or workload identity.
- **RFC 2136 (TSIG dynamic update)** — one implementation covers BIND, PowerDNS, Knot, and standards-compliant Microsoft DNS over the wire. Highest coverage-per-effort ratio in the entire roadmap. Needs a UDP/TCP DNS message library (likely `DnsClient` or a minimal hand-rolled implementation for UPDATE).

#### Cert renewal worker
- `IHostedService` inside `DnsGoBetween.Api`: daily check at startup + cron, re-enroll (AD CS or ACME, depending on cert source) at 75% of validity.
- Pending-approval polling for AD CS templates that require CA manager approval (request ID persisted across service restarts in registry).
- Status surfaced on the existing `HealthView.razor` (next renewal time, last attempt result, alert thresholds).

### v1.6 — Auth Modernization

The current Basic + Negotiate model serves internal/domain-joined use. Modern enterprise deployments need federated identity and automation-friendly token auth.

#### Microsoft Entra ID (OIDC) *(primary)*
- `Microsoft.Identity.Web` integration: cookie auth + redirect for the Blazor UI, JWT bearer for the API.
- Token validation honours tenant, audience, allowed issuers from `appsettings.json`.
- Group claims surface as ClaimTypes.Role via a `GroupToRoleMappings` config block: `{ "<group-guid>": "Admin" }` — same shape will be reused for OIDC and SAML in later versions, so the role-source abstraction is decoupled from identity provider.

#### Personal Access Tokens (PATs) *(automation)*
- Admin-issued, hashed at rest with Argon2id, prefix-displayed for management.
- Scoped to a subset of issuer's roles (least-privilege automation tokens).
- Revocable from the admin UI; recorded in audit log on create/revoke/use.
- Required for scripts that can't do interactive Entra/SAML flows (e.g. existing `acme/dns-challenge-create.ps1`).

#### Generic OIDC *(bonus)*
- Same `Microsoft.AspNetCore.Authentication.OpenIdConnect` pipeline with provider-agnostic config — supports Okta, Auth0, Keycloak, AWS IAM Identity Center, Google Workspace.
- Mostly config + docs once Entra is working.

#### Audit log enhancements
- Add `AuthMethod` field (`Basic`/`Negotiate`/`Entra`/`OIDC`/`PAT`) to every audit entry. Pairs with the existing correlation ID. Required for forensics in mixed-auth deployments.

**Backwards compat:** Basic + Negotiate stay. New auth methods are additive and individually toggleable. Existing deployments continue working untouched.

**Dependency:** v1.6 should ship AFTER v1.4 (specifically AD CS / ACME) — Entra needs the app reachable on a valid-cert URL, which the new cert station makes materially easier.

### v1.7 — Enterprise SSO + Fine-grained RBAC

- **SAML 2.0** — for shops standardised on Okta/OneLogin/ADFS/PingFederate/JumpCloud. Library: `Sustainsys.Saml2`. Heavier lift than OIDC (XML signing, assertion validation, metadata exchange).
- **Admin UI for role management** — replace the `appsettings.json` `GroupToRoleMappings` and PAT lists with a small admin page. Mappings persisted in a local SQLite DB (already a viable storage option since audit history will likely move there). Audit log captures all mapping changes.
- **Per-zone RBAC** *(optional, multi-tenant)* — extend the model from `Role × Provider` to `Role × Provider × Zone`. Allows scoping a team to only their own zones across providers. Larger change; ship only on customer request.

### v1.8+ — Speculative

- ACME outbound for the API's own TLS cert (eat our own dog food — currently v1.4b is for *issuing* certs to callers; this would replace the AD CS / PFX self-cert path with an ACME-issued public cert for the management UI itself).
- Additional registrar APIs (GoDaddy, Porkbun, Name.com, Gandi, IONOS) on user demand — each is ~1 dev day of glue code mirroring `NamecheapDnsProvider`.
- Bulk import/export (BIND zone file format).
- Record edit (in-place update) endpoint, complementing the current add/delete-only model.

