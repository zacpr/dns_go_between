<#
.SYNOPSIS
    ACME DNS-01 challenge create hook for DnsGoBetween.

.DESCRIPTION
    Creates the _acme-challenge TXT record required for DNS-01 certificate validation.

    Configuration (environment variables):
      DNSGOBET_URL              Base URL of DnsGoBetween API, e.g. https://dnsserver:6790
      DNSGOBET_USER             Username for Basic auth
      DNSGOBET_PASS             Password for Basic auth
      DNSGOBET_ZONE             (optional) Zone name, e.g. example.com
                                If omitted the script auto-detects the best matching zone
                                by calling GET /api/zones.
      DNSGOBET_SKIP_TLS_VERIFY  Set to "1" or "true" to ignore self-signed certs (default: false)
      DNSGOBET_PROPAGATION_WAIT Seconds to wait after adding the record (default: 30)

    Certbot integration (env vars set automatically by certbot):
      CERTBOT_DOMAIN      Domain being validated, e.g. sub.example.com
      CERTBOT_VALIDATION  Token string to publish as TXT data

.PARAMETER Domain
    Domain being validated. Defaults to $env:CERTBOT_DOMAIN.

.PARAMETER Token
    Validation token. Defaults to $env:CERTBOT_VALIDATION.

.EXAMPLE
    # Certbot (env vars supplied by certbot automatically)
    certbot certonly --manual --preferred-challenges dns \
        --manual-auth-hook   "pwsh -File /opt/acme/dns-challenge-create.ps1" \
        --manual-cleanup-hook "pwsh -File /opt/acme/dns-challenge-delete.ps1" \
        -d example.com -d "*.example.com"

.EXAMPLE
    # win-acme  (store creds in env before running win-acme)
    $env:DNSGOBET_URL  = "https://dnsserver:6790"
    $env:DNSGOBET_USER = "dnsadmin"
    $env:DNSGOBET_PASS = "s3cret"
    .\dns-challenge-create.ps1 -Domain example.com -Token "abc123token"

.EXAMPLE
    # Standalone / acme.sh manual hook
    DNSGOBET_URL=https://dnsserver:6790 DNSGOBET_USER=dnsadmin DNSGOBET_PASS=s3cret \
    CERTBOT_DOMAIN=example.com CERTBOT_VALIDATION=abc123token \
    pwsh -File dns-challenge-create.ps1
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
    $bytes  = [System.Text.Encoding]::UTF8.GetBytes("${User}:${Pass}")
    $b64    = [System.Convert]::ToBase64String($bytes)
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
        Method             = $Method
        Uri                = $Uri
        Headers            = $Headers
        ContentType        = 'application/json'
        UseBasicParsing    = $true
        ErrorAction        = 'Stop'
    }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Compress)
    }
    if ($SkipVerify) {
        # PowerShell 6+ / pwsh
        $params.SkipCertificateCheck = $true
    }
    return Invoke-RestMethod @params
}

function Resolve-Zone {
    param([string]$Domain, [hashtable]$Headers, [string]$BaseUrl, [bool]$SkipVerify)
    Write-Log 'INFO' "Auto-detecting zone for domain '$Domain'..."
    try {
        $zones = Invoke-Api -Method GET -Uri "$BaseUrl/api/zones" -Headers $Headers -SkipVerify $SkipVerify
        # Pick the longest zone name that is a suffix of the domain
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
    # Full challenge FQDN is _acme-challenge.<domain>
    # Strip the zone suffix to get the relative hostname
    $fullFqdn = "_acme-challenge.$Domain"
    $suffix   = ".$Zone"
    if ($fullFqdn -eq $Zone) {
        return '@'
    }
    if ($fullFqdn.EndsWith($suffix)) {
        return $fullFqdn.Substring(0, $fullFqdn.Length - $suffix.Length)
    }
    throw "Challenge FQDN '$fullFqdn' does not end with zone '$Zone'"
}

#endregion

#region ── main ───────────────────────────────────────────────────────────────

Write-Log 'INFO' "=== DnsGoBetween ACME DNS-01 Create ==="

# Validate parameters
if ([string]::IsNullOrWhiteSpace($Domain)) {
    Write-Log 'ERROR' 'Domain is required. Pass -Domain or set CERTBOT_DOMAIN.'
    exit 1
}
if ([string]::IsNullOrWhiteSpace($Token)) {
    Write-Log 'ERROR' 'Token is required. Pass -Token or set CERTBOT_VALIDATION.'
    exit 1
}

# Read config
$baseUrl     = Get-Config 'DNSGOBET_URL'
$user        = Get-Config 'DNSGOBET_USER'
$pass        = Get-Config 'DNSGOBET_PASS'
$zoneHint    = Get-Config 'DNSGOBET_ZONE'
$skipVerify  = (Get-Config 'DNSGOBET_SKIP_TLS_VERIFY') -in @('1','true','yes')
$propWait    = [int](Get-Config 'DNSGOBET_PROPAGATION_WAIT' '30')

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

# Resolve zone
$zone = if (-not [string]::IsNullOrWhiteSpace($zoneHint)) {
    Write-Log 'INFO' "Using explicit zone: '$zoneHint'"
    $zoneHint
} else {
    Resolve-Zone -Domain $Domain -Headers $headers -BaseUrl $baseUrl -SkipVerify $skipVerify
}

$hostname = Get-ChallengeHostname -Domain $Domain -Zone $zone
Write-Log 'INFO' "TXT hostname: '$hostname' in zone '$zone'"

# Create TXT record (retry up to 3 times)
$body = @{
    zoneName   = $zone
    hostName   = $hostname
    recordType = 'TXT'
    data       = $Token
    timeToLive = 60
}

$maxAttempts = 3
$attempt     = 0
$created     = $false

while (-not $created -and $attempt -lt $maxAttempts) {
    $attempt++
    try {
        Write-Log 'INFO' "Creating TXT record (attempt $attempt/$maxAttempts)..."
        Invoke-Api -Method POST -Uri "$baseUrl/api/records" -Headers $headers -Body $body -SkipVerify $skipVerify | Out-Null
        $created = $true
        Write-Log 'INFO' "TXT record created successfully."
    }
    catch {
        $statusCode = $null
        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        if ($statusCode -eq 409) {
            # Record already exists — treat as success (idempotent)
            Write-Log 'INFO' "TXT record already exists (HTTP 409) — treating as success."
            $created = $true
        }
        elseif ($attempt -lt $maxAttempts) {
            $wait = [math]::Pow(2, $attempt)  # 2s, 4s
            Write-Log 'WARN' "Attempt $attempt failed: $_  Retrying in ${wait}s..."
            Start-Sleep -Seconds $wait
        }
        else {
            Write-Log 'ERROR' "Failed to create TXT record after $maxAttempts attempts: $_"
            exit 1
        }
    }
}

# Wait for DNS propagation
if ($propWait -gt 0) {
    Write-Log 'INFO' "Waiting ${propWait}s for DNS propagation..."
    Start-Sleep -Seconds $propWait
}

Write-Log 'INFO' "Challenge record published. Validation can proceed."
exit 0

#endregion
