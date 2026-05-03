# ACME DNS-01 Hooks for DnsGoBetween

PowerShell hook scripts for automating DNS-01 ACME certificate validation via DnsGoBetween's REST API.

The scripts create and remove `_acme-challenge` TXT records, which ACME clients (Certbot, win-acme, acme.sh) use to prove domain ownership without opening inbound HTTP ports.

---

## Requirements

- **DnsGoBetween ≥ 1.3.6** (TXT record support)
- PowerShell 7+ (`pwsh`) — or Windows PowerShell 5.1 (tested; TLS skip requires pwsh)
- A DNS-admin account configured in DnsGoBetween's `AllowedZones`

---

## Environment variables

| Variable                  | Required | Description                                                         |
|---------------------------|----------|---------------------------------------------------------------------|
| `DNSGOBET_URL`            | ✅       | Base URL, e.g. `https://dnsserver:6790`                             |
| `DNSGOBET_USER`           | ✅       | Username for Basic auth                                             |
| `DNSGOBET_PASS`           | ✅       | Password for Basic auth                                             |
| `DNSGOBET_ZONE`           | —        | Zone name (e.g. `example.com`). Auto-detected from `/api/zones` if omitted. |
| `DNSGOBET_SKIP_TLS_VERIFY`| —        | Set `1` to skip TLS certificate validation (for self-signed certs) |
| `DNSGOBET_PROPAGATION_WAIT` | —      | Seconds to wait after creating the record (default: `30`)           |

---

## Certbot

```bash
export DNSGOBET_URL=https://dnsserver:6790
export DNSGOBET_USER=dnsadmin
export DNSGOBET_PASS=s3cret
# Optional — set if DnsGoBetween uses a self-signed cert
# export DNSGOBET_SKIP_TLS_VERIFY=1

certbot certonly \
  --manual \
  --preferred-challenges dns \
  --manual-auth-hook    "pwsh -File /opt/acme/dns-challenge-create.ps1" \
  --manual-cleanup-hook "pwsh -File /opt/acme/dns-challenge-delete.ps1" \
  -d example.com \
  -d "*.example.com"
```

Certbot automatically sets `CERTBOT_DOMAIN` and `CERTBOT_VALIDATION` before calling each hook.

For non-interactive renewal add `--non-interactive` and store credentials in a persistent location (e.g. `/etc/letsencrypt/renewal-hooks/pre/dnsgobet-creds.sh`).

---

## win-acme

win-acme's **Script** validation plugin (`--validation script`) calls your scripts with positional arguments.

1. In the win-acme configuration wizard choose **Manual input** → **DNS (Script)**.
2. Point it at the scripts:

```
Create script : C:\acme\dns-challenge-create.ps1
Delete script : C:\acme\dns-challenge-delete.ps1
Script arguments (create): -Domain {0} -Token {1}
Script arguments (delete): -Domain {0} -Token {1}
```

3. Set the environment variables before launching win-acme (or add them to a wrapper `.ps1`):

```powershell
$env:DNSGOBET_URL  = "https://dnsserver:6790"
$env:DNSGOBET_USER = "dnsadmin"
$env:DNSGOBET_PASS = "s3cret"
wacs.exe
```

---

## acme.sh

acme.sh supports custom DNS hooks via `--dns dns_script`. Create two thin wrapper shell scripts:

**`dns_dnsgobet_add.sh`**
```bash
#!/usr/bin/env bash
# Called by acme.sh as: dns_dnsgobet_add <fqdn> <token>
export CERTBOT_DOMAIN="${1#_acme-challenge.}"
export CERTBOT_VALIDATION="$2"
pwsh -File "$(dirname "$0")/dns-challenge-create.ps1"
```

**`dns_dnsgobet_rm.sh`**
```bash
#!/usr/bin/env bash
export CERTBOT_DOMAIN="${1#_acme-challenge.}"
export CERTBOT_VALIDATION="$2"
pwsh -File "$(dirname "$0")/dns-challenge-delete.ps1"
```

Then issue:
```bash
export DNSGOBET_URL=https://dnsserver:6790
export DNSGOBET_USER=dnsadmin
export DNSGOBET_PASS=s3cret

acme.sh --issue \
  --dns dns_dnsgobet \
  -d example.com \
  -d "*.example.com" \
  --dnssleep 35
```

> Set `--dnssleep` to match or exceed `DNSGOBET_PROPAGATION_WAIT`.

---

## Standalone / testing

```powershell
$env:DNSGOBET_URL  = "https://dnsserver:6790"
$env:DNSGOBET_USER = "dnsadmin"
$env:DNSGOBET_PASS = "s3cret"
$env:DNSGOBET_SKIP_TLS_VERIFY = "1"   # if self-signed
$env:DNSGOBET_PROPAGATION_WAIT = "5"  # short wait for testing

# Create
.\dns-challenge-create.ps1 -Domain sub.example.com -Token "testtoken123"

# Verify (from another machine / DNS client)
Resolve-DnsName -Name "_acme-challenge.sub.example.com" -Type TXT

# Cleanup
.\dns-challenge-delete.ps1 -Domain sub.example.com -Token "testtoken123"
```

---

## How it works

| Step | Script | API call |
|------|--------|----------|
| 1 | `dns-challenge-create.ps1` | `GET /api/zones` → auto-detect zone (unless `DNSGOBET_ZONE` set) |
| 2 | `dns-challenge-create.ps1` | `POST /api/records` → create `_acme-challenge.<domain>` TXT with 60 s TTL |
| 3 | *(wait for propagation)* | — |
| 4 | ACME client | validates TXT record against authoritative DNS |
| 5 | `dns-challenge-delete.ps1` | `DELETE /api/records` → remove the TXT record |

The create script retries up to 3 times with exponential back-off. HTTP 409 (already exists) is treated as success. The cleanup script treats HTTP 404 (already gone) as success and does not block cert issuance even if the delete call fails.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `DNSGOBET_URL environment variable is required` | Config missing | Export env vars before running |
| `No matching zone found` | Domain not in an allowed zone | Set `DNSGOBET_ZONE` explicitly, or add the zone to `AllowedZones` in appsettings.json |
| HTTP 401 | Wrong credentials | Check `DNSGOBET_USER` / `DNSGOBET_PASS` |
| HTTP 403 | Account not in DnsAdmins | Add the account to the DnsAdmins group on the DNS server |
| TLS errors | Self-signed cert | Set `DNSGOBET_SKIP_TLS_VERIFY=1` (requires pwsh 7+) |
| Validation fails despite record existing | Propagation too short | Increase `DNSGOBET_PROPAGATION_WAIT` (try 60 or 120) |
