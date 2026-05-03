<#
.SYNOPSIS
    ACME DNS-01 challenge cleanup hook for DnsGoBetween.

.DESCRIPTION
    Removes the _acme-challenge TXT record after DNS-01 certificate validation completes.

    Configuration (environment variables):
      DNSGOBET_URL              Base URL of DnsGoBetween API, e.g. https://dnsserver:6790
      DNSGOBET_USER             Username for Basic auth
      DNSGOBET_PASS             Password for Basic auth
      DNSGOBET_ZONE             (optional) Zone name. Auto-detected if omitted.
      DNSGOBET_SKIP_TLS_VERIFY  Set to "1" or "true" to ignore self-signed certs (default: false)

    Certbot integration (env vars set automatically by certbot):
      CERTBOT_DOMAIN      Domain that was validated
      CERTBOT_VALIDATION  Token that was published

.PARAMETER Domain
    Domain that was validated. Defaults to $env:CERTBOT_DOMAIN.

.PARAMETER Token
    Validation token that was published. Defaults to $env:CERTBOT_VALIDATION.

.EXAMPLE
    certbot certonly --manual --preferred-challenges dns \
        --manual-auth-hook    "pwsh -File /opt/acme/dns-challenge-create.ps1" \
        --manual-cleanup-hook "pwsh -File /opt/acme/dns-challenge-delete.ps1" \
        -d example.com -d "*.example.com"
#>
[CmdletBinding()]
param(
    [string]$Domain = $env:CERTBOT_DOMAIN,
    [string]$Token  = $env:CERTBOT_VALIDATION
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

#region ── helpers ────────────────────────────────────────────────────────────

function Write-Log {
    param([string]$Level, [string]$Message)
    $ts = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Write-Host "[$ts] [$Level] $Message"
}

function Get-Config {
    param([string]$Name, [string]$Default = '')
    $val = [System.Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($val)) { return $Default }
    return $val.Trim()
}

function Get-AuthHeader {
    param([string]$User, [string]$Pass)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes("${User}:${Pass}")
    $b64   = [System.Convert]::ToBase64String($bytes)
    return @{ Authorization = "Basic $b64" }
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body,
        [bool]$SkipVerify
    )
    $params = @{
        Method          = $Method
        Uri             = $Uri
        Headers         = $Headers
        ContentType     = 'application/json'
        UseBasicParsing = $true
        ErrorAction     = 'Stop'
    }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Compress)
    }
    if ($SkipVerify) {
        $params.SkipCertificateCheck = $true
    }
    return Invoke-RestMethod @params
}

function Resolve-Zone {
    param([string]$Domain, [hashtable]$Headers, [string]$BaseUrl, [bool]$SkipVerify)
    Write-Log 'INFO' "Auto-detecting zone for domain '$Domain'..."
    try {
        $zones = Invoke-Api -Method GET -Uri "$BaseUrl/api/zones" -Headers $Headers -SkipVerify $SkipVerify
        $best = $zones |
            Where-Object { $Domain -eq $_ -or $Domain.EndsWith(".$_") } |
            Sort-Object { $_.Length } -Descending |
            Select-Object -First 1
        if (-not $best) {
            throw "No matching zone found for '$Domain' among: $($zones -join ', ')"
        }
        Write-Log 'INFO' "Resolved zone: '$best'"
        return $best
    }
    catch {
        throw "Zone auto-detection failed: $_"
    }
}

function Get-ChallengeHostname {
    param([string]$Domain, [string]$Zone)
    $fullFqdn = "_acme-challenge.$Domain"
    $suffix   = ".$Zone"
    if ($fullFqdn -eq $Zone) { return '@' }
    if ($fullFqdn.EndsWith($suffix)) {
        return $fullFqdn.Substring(0, $fullFqdn.Length - $suffix.Length)
    }
    throw "Challenge FQDN '$fullFqdn' does not end with zone '$Zone'"
}

#endregion

#region ── main ───────────────────────────────────────────────────────────────

Write-Log 'INFO' "=== DnsGoBetween ACME DNS-01 Cleanup ==="

if ([string]::IsNullOrWhiteSpace($Domain)) {
    Write-Log 'ERROR' 'Domain is required. Pass -Domain or set CERTBOT_DOMAIN.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Log 'ERROR' 'Token is required. Pass -Token or set CERTBOT_VALIDATION.'
    exit 1
}

$baseUrl    = Get-Config 'DNSGOBET_URL'
$user       = Get-Config 'DNSGOBET_USER'
$pass       = Get-Config 'DNSGOBET_PASS'
$zoneHint   = Get-Config 'DNSGOBET_ZONE'
$skipVerify = (Get-Config 'DNSGOBET_SKIP_TLS_VERIFY') -in @('1','true','yes')

if ([string]::IsNullOrWhiteSpace($baseUrl)) {
    Write-Log 'ERROR' 'DNSGOBET_URL environment variable is required.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($pass)) {
    Write-Log 'ERROR' 'DNSGOBET_USER and DNSGOBET_PASS environment variables are required.'
    exit 1
}

$baseUrl = $baseUrl.TrimEnd('/')
$headers = Get-AuthHeader -User $user -Pass $pass

Write-Log 'INFO' "Domain: $Domain"
Write-Log 'INFO' "API:    $baseUrl"

$zone = if (-not [string]::IsNullOrWhiteSpace($zoneHint)) {
    Write-Log 'INFO' "Using explicit zone: '$zoneHint'"
    $zoneHint
} else {
    Resolve-Zone -Domain $Domain -Headers $headers -BaseUrl $baseUrl -SkipVerify $skipVerify
}

$hostname = Get-ChallengeHostname -Domain $Domain -Zone $zone
Write-Log 'INFO' "Deleting TXT '$hostname' in zone '$zone'..."

$body = @{
    zoneName   = $zone
    hostName   = $hostname
    recordType = 'TXT'
    data       = $Token
}

try {
    Invoke-Api -Method DELETE -Uri "$baseUrl/api/records" -Headers $headers -Body $body -SkipVerify $skipVerify | Out-Null
    Write-Log 'INFO' "TXT record deleted successfully."
}
catch {
    $statusCode = $null
    if ($_.Exception.Response) {
        $statusCode = [int]$_.Exception.Response.StatusCode
    }

    if ($statusCode -eq 404) {
        # Record already gone — not an error during cleanup
        Write-Log 'INFO' "TXT record not found (HTTP 404) — already removed, nothing to do."
    }
    else {
        Write-Log 'WARN' "Failed to delete TXT record (HTTP $statusCode): $_ — continuing anyway."
        # Exit 0: cleanup failures should not block cert issuance which has already succeeded
    }
}

Write-Log 'INFO' "Cleanup complete."
exit 0

#endregion
