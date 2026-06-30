#Requires -Version 5.1
<#
.SYNOPSIS
    Finds and (optionally) deletes DNS records that were stored with
    fully-qualified names inside their own zone -- e.g. record name
    'all.example.com' stored inside zone 'example.com'. Such records
    actually answer queries for 'all.example.com.example.com', which
    is almost always an unintended duplicate caused by API misuse.

.DESCRIPTION
    Walks every zone (or the zones you specify), GETs the record list
    via the DNS Go-Between REST API, and looks for entries whose stored
    HostName ends with '.<zone>'. By default it operates in DRY-RUN
    mode -- nothing is deleted; you get a table of what *would* be
    removed. Pass -Apply to actually issue DELETE requests.

    Safety rule: a record is only flagged for deletion when an
    equivalent record exists in the same zone with the short-form
    HostName AND the same RecordType AND the same Data. If the
    FQDN-named record is unique, it is reported with a [UNIQUE]
    warning and left in place -- you must delete it manually after
    deciding whether it was intentional. Apex records (HostName
    exactly equal to the zone name) are always left alone.

    Authentication uses your current Windows identity by default; pass
    -Credential to use Basic Auth.

.PARAMETER BaseUrl
    Base URL of the running service. Defaults to https://localhost:6790.

.PARAMETER Zone
    Optional list of zone names to scan. If omitted, every zone returned
    by GET /api/zones is scanned.

.PARAMETER Provider
    DNS provider name (default 'Windows'). Cloud providers like
    Cloudflare / Route 53 already normalise this on read, so this script
    is most useful against the Windows providers.

.PARAMETER Apply
    Switch. Without this, the script only reports (dry run). With this,
    matching duplicates are actually deleted via DELETE /api/{provider}/records.

.PARAMETER Credential
    Optional PSCredential for Basic Auth. If omitted, current Windows
    credentials are used (UseDefaultCredentials).

.PARAMETER SkipCertificateCheck
    Skip TLS validation. Useful for local dev with self-signed certs.

.EXAMPLE
    # Dry run against the local service for every zone, current creds.
    .\scripts\cleanup-fqdn-duplicates.ps1

.EXAMPLE
    # Apply for a single zone using Basic Auth.
    $cred = Get-Credential
    .\scripts\cleanup-fqdn-duplicates.ps1 -Zone ashurtech.net -Apply -Credential $cred

.EXAMPLE
    # Dry run against a remote service, self-signed cert.
    .\scripts\cleanup-fqdn-duplicates.ps1 -BaseUrl https://dns01.lab.local:6790 -SkipCertificateCheck
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string] $BaseUrl = "https://localhost:6790",
    [string[]] $Zone,
    [string] $Provider = "Windows",
    [switch] $Apply,
    [pscredential] $Credential,
    [switch] $SkipCertificateCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ("-" * $Text.Length) -ForegroundColor DarkCyan
}

function Invoke-DnsApi {
    param(
        [Parameter(Mandatory)] [ValidateSet("GET", "POST", "DELETE")] [string] $Method,
        [Parameter(Mandatory)] [string] $Url,
        [object] $Body
    )

    $invokeParams = @{
        Uri         = $Url
        Method      = $Method
        TimeoutSec  = 30
        ErrorAction = 'Stop'
    }

    if ($Credential) {
        $invokeParams.Authentication = 'Basic'
        $invokeParams.Credential = $Credential
        $invokeParams.AllowUnencryptedAuthentication = $false
    }
    else {
        $invokeParams.UseDefaultCredentials = $true
    }

    if ($SkipCertificateCheck -and $PSVersionTable.PSVersion.Major -ge 6) {
        $invokeParams.SkipCertificateCheck = $true
    }

    if ($null -ne $Body) {
        $invokeParams.ContentType = 'application/json'
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 10)
    }

    try {
        return Invoke-RestMethod @invokeParams
    }
    catch {
        $resp = $_.Exception.Response
        $status = if ($resp) { [int]$resp.StatusCode } else { 'n/a' }
        $bodyText = ""
        if ($resp -and $resp.GetResponseStream) {
            try {
                $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $bodyText = $reader.ReadToEnd()
                $reader.Dispose()
            }
            catch { }
        }
        throw "HTTP $status from $Method $Url. $bodyText"
    }
}

# Old-PS hack: disable TLS validation globally when the modern -SkipCertificateCheck switch
# isn't available (Windows PowerShell 5.1 falls back to this).
if ($SkipCertificateCheck -and $PSVersionTable.PSVersion.Major -lt 6) {
    Add-Type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCerts {
    public static bool Validate(object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; }
}
"@ -ErrorAction SilentlyContinue
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback =
        [System.Net.Security.RemoteCertificateValidationCallback]{ param($s, $c, $ch, $e) return $true }
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
}

$normalizedBaseUrl = $BaseUrl.TrimEnd('/')

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  DNS Go-Between -- FQDN duplicate cleanup" -ForegroundColor Cyan
Write-Host "  Base URL : $normalizedBaseUrl"             -ForegroundColor Cyan
Write-Host "  Provider : $Provider"                       -ForegroundColor Cyan
Write-Host "  Mode     : $(if ($Apply) { 'APPLY (will delete)' } else { 'DRY RUN' })" `
    -ForegroundColor $(if ($Apply) { 'Yellow' } else { 'Green' })
Write-Host "============================================" -ForegroundColor Cyan

# ----- Discover zones --------------------------------------------------------
Write-Step "Discovering zones"

$targetZones = @()
if ($Zone -and $Zone.Count -gt 0) {
    $targetZones = $Zone
    Write-Host "  Using $($Zone.Count) caller-supplied zone(s)."
}
else {
    $zones = @(Invoke-DnsApi -Method GET -Url "$normalizedBaseUrl/api/$Provider/zones")
    $targetZones = $zones | ForEach-Object { $_.Name }
    Write-Host "  Discovered $($targetZones.Count) zone(s) from /api/$Provider/zones."
}

if ($targetZones.Count -eq 0) {
    Write-Host "  No zones to scan. Exiting." -ForegroundColor Yellow
    return
}

# ----- Scan each zone --------------------------------------------------------
$summary = New-Object System.Collections.Generic.List[object]
$toDelete = New-Object System.Collections.Generic.List[object]
$uniqueOddities = New-Object System.Collections.Generic.List[object]

foreach ($zoneName in $targetZones) {
    Write-Step "Scanning zone: $zoneName"

    try {
        $records = @(Invoke-DnsApi -Method GET -Url "$normalizedBaseUrl/api/$Provider/zones/$zoneName/records")
    }
    catch {
        Write-Host "  [SKIP] Failed to list records: $($_.Exception.Message)" -ForegroundColor Yellow
        continue
    }

    $zoneSuffix = "." + $zoneName
    $apexLiteral = $zoneName

    # Build an index of (DisplayName, RecordType, Data) -> existing short-form records.
    # We only delete an FQDN-named record when an equivalent short-form record exists.
    $shortIndex = @{}
    foreach ($r in $records) {
        $hostName = [string]$r.HostName
        $isFqdnStyle =
            ([string]::Equals($hostName, $apexLiteral, [StringComparison]::OrdinalIgnoreCase)) -or
            ($hostName.Length -gt $zoneSuffix.Length -and
             $hostName.EndsWith($zoneSuffix, [System.StringComparison]::OrdinalIgnoreCase))

        if (-not $isFqdnStyle) {
            $key = ("{0}|{1}|{2}" -f $hostName.ToLowerInvariant(), $r.RecordType, $r.Data)
            if (-not $shortIndex.ContainsKey($key)) {
                $shortIndex[$key] = $r
            }
        }
    }

    $fqdnFlagged = 0
    foreach ($r in $records) {
        $hostName = [string]$r.HostName

        # Apex records (HostName == ZoneName) are legitimate -- skip.
        if ([string]::Equals($hostName, $apexLiteral, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        # Only interested in records whose stored name ends with the zone suffix.
        if (-not ($hostName.Length -gt $zoneSuffix.Length -and
                  $hostName.EndsWith($zoneSuffix, [System.StringComparison]::OrdinalIgnoreCase))) {
            continue
        }

        $fqdnFlagged++
        $displayName = $hostName.Substring(0, $hostName.Length - $zoneSuffix.Length)
        $twinKey = ("{0}|{1}|{2}" -f $displayName.ToLowerInvariant(), $r.RecordType, $r.Data)

        if ($shortIndex.ContainsKey($twinKey)) {
            $toDelete.Add([pscustomobject]@{
                Zone        = $zoneName
                StoredName  = $hostName
                ShortName   = $displayName
                Type        = $r.RecordType
                Data        = $r.Data
                TTL         = $r.TimeToLive
                Status      = 'DUPLICATE'
            }) | Out-Null
        }
        else {
            $uniqueOddities.Add([pscustomobject]@{
                Zone        = $zoneName
                StoredName  = $hostName
                Type        = $r.RecordType
                Data        = $r.Data
                TTL         = $r.TimeToLive
                Status      = 'UNIQUE'
            }) | Out-Null
        }
    }

    $summary.Add([pscustomobject]@{
        Zone         = $zoneName
        TotalRecords = $records.Count
        FqdnStored   = $fqdnFlagged
    }) | Out-Null

    Write-Host ("  Records scanned: {0}, FQDN-stored: {1}" -f $records.Count, $fqdnFlagged)
}

# ----- Report ----------------------------------------------------------------
Write-Step "Zone summary"
$summary | Format-Table -AutoSize | Out-Host

if ($uniqueOddities.Count -gt 0) {
    Write-Step "FQDN-stored records WITHOUT a short-form twin ($($uniqueOddities.Count))"
    Write-Host "  These are NOT deleted -- they have no equivalent short-form record,"
    Write-Host "  so removing them could destroy unique data. Review and delete manually" -ForegroundColor Yellow
    Write-Host "  via the UI or the API if they are indeed unwanted." -ForegroundColor Yellow
    $uniqueOddities | Format-Table -AutoSize | Out-Host
}

if ($toDelete.Count -eq 0) {
    Write-Step "No safely-deletable FQDN duplicates found"
    Write-Host "  Nothing to do." -ForegroundColor Green
    return
}

Write-Step "FQDN-stored duplicates eligible for deletion ($($toDelete.Count))"
$toDelete | Format-Table -AutoSize | Out-Host

if (-not $Apply) {
    Write-Host ""
    Write-Host "DRY RUN. No changes have been made." -ForegroundColor Green
    Write-Host "Re-run with -Apply to delete the records listed above." -ForegroundColor Green
    return
}

# ----- Apply -----------------------------------------------------------------
Write-Step "Applying deletions"

$deleted = 0
$failed = 0
foreach ($entry in $toDelete) {
    $target = "{0} {1} {2} -> {3}" -f $entry.Zone, $entry.StoredName, $entry.Type, $entry.Data
    if (-not $PSCmdlet.ShouldProcess($target, "DELETE FQDN-duplicate record")) {
        continue
    }

    try {
        $body = @{
            ZoneName   = $entry.Zone
            HostName   = $entry.StoredName
            RecordType = $entry.Type
            Data       = $entry.Data
        }
        Invoke-DnsApi -Method DELETE -Url "$normalizedBaseUrl/api/$Provider/records" -Body $body | Out-Null
        Write-Host "  [OK]   $target" -ForegroundColor Green
        $deleted++
    }
    catch {
        Write-Host "  [FAIL] $target -- $($_.Exception.Message)" -ForegroundColor Red
        $failed++
    }
}

Write-Step "Done"
Write-Host ("  Deleted : {0}" -f $deleted) -ForegroundColor Green
if ($failed -gt 0) {
    Write-Host ("  Failed  : {0}" -f $failed) -ForegroundColor Red
    exit 1
}
