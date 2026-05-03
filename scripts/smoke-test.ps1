#Requires -Version 5.1
<#
.SYNOPSIS
    Runs smoke tests against a live DNS Go-Between deployment.

.DESCRIPTION
    Verifies the basic service surface:
      - /health/live and /health/ready
      - DNS Manager page load
      - GET /api/zones
      - GET /api/zones/{zone}/records
      - optional add/delete round-trip for a temporary A record

    By default this uses the current Windows identity for authentication.

.PARAMETER BaseUrl
    Base URL of the running service.

.PARAMETER Zone
    Zone to use for read tests and optional write tests.

.PARAMETER RunWriteTests
    When set, creates a temporary A record, verifies it exists, deletes it,
    and verifies it is gone.

.PARAMETER HostName
    Optional fixed test host name. If omitted, a random smoke-test host name is used.

.PARAMETER IPv4Address
    IPv4 address to use for the optional write test.

.EXAMPLE
    .\scripts\smoke-test.ps1 -Zone ashurtech.net

.EXAMPLE
    .\scripts\smoke-test.ps1 -Zone ashurtech.net -RunWriteTests
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = "http://localhost:6790",
    [Parameter(Mandatory)]
    [string] $Zone,
    [switch] $RunWriteTests,
    [string] $HostName,
    [string] $IPv4Address = "192.168.1.250"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$Text) {
    Write-Host "`n$Text" -ForegroundColor Cyan
    Write-Host ("-" * $Text.Length) -ForegroundColor DarkCyan
}

function Write-Pass([string]$Text) {
    Write-Host "  [PASS] $Text" -ForegroundColor Green
}

function Write-Fail([string]$Text) {
    Write-Host "  [FAIL] $Text" -ForegroundColor Red
}

function Invoke-ApiRequest {
    param(
        [Parameter(Mandatory)] [ValidateSet("GET", "POST", "DELETE")] [string] $Method,
        [Parameter(Mandatory)] [string] $Url,
        [object] $Body
    )

    $invokeParams = @{
        Uri = $Url
        Method = $Method
        UseDefaultCredentials = $true
        TimeoutSec = 20
        ErrorAction = 'Stop'
    }

    if ($Body -ne $null) {
        $invokeParams.ContentType = 'application/json'
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 10)
    }

    try {
        return Invoke-RestMethod @invokeParams
    }
    catch {
        if ($_.Exception.Response) {
            $response = $_.Exception.Response
            $statusCode = [int]$response.StatusCode
            $reader = $null
            $bodyText = ""

            try {
                $stream = $response.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $bodyText = $reader.ReadToEnd()
                }
            }
            finally {
                if ($reader) {
                    $reader.Dispose()
                }
            }

            throw "HTTP $statusCode from $Url. Body: $bodyText"
        }

        throw
    }
}

function Invoke-WebCheck {
    param(
        [Parameter(Mandatory)] [string] $Url
    )

    try {
        return Invoke-WebRequest -Uri $Url -UseDefaultCredentials -TimeoutSec 20 -ErrorAction Stop
    }
    catch {
        throw "Web request failed for $Url. $($_.Exception.Message)"
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$normalizedBaseUrl = $BaseUrl.TrimEnd('/')
$effectiveHostName = if ([string]::IsNullOrWhiteSpace($HostName)) {
    "smoketest-" + ([Guid]::NewGuid().ToString('N').Substring(0, 8))
} else {
    $HostName
}

$results = New-Object System.Collections.Generic.List[object]

function Run-Test {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Action
    )

    try {
        & $Action
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'PASS' }) | Out-Null
        Write-Pass $Name
    }
    catch {
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'FAIL'; Detail = $_.Exception.Message }) | Out-Null
        Write-Fail ("{0} - {1}" -f $Name, $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  DNS Go-Between Smoke Tests" -ForegroundColor Cyan
Write-Host "  Base URL : $normalizedBaseUrl" -ForegroundColor Cyan
Write-Host "  Zone     : $Zone" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

Write-Step "Read checks"

Run-Test "GET /health/live returns 200" {
    $response = Invoke-WebCheck -Url "$normalizedBaseUrl/health/live"
    Assert-True ($response.StatusCode -eq 200) "/health/live did not return HTTP 200."
}

Run-Test "GET /health/ready returns 200" {
    $response = Invoke-WebCheck -Url "$normalizedBaseUrl/health/ready"
    Assert-True ($response.StatusCode -eq 200) "/health/ready did not return HTTP 200."
}

Run-Test "GET / loads DNS Manager page" {
    $response = Invoke-WebCheck -Url "$normalizedBaseUrl/"
    Assert-True ($response.StatusCode -eq 200) "Root page did not return HTTP 200."
    Assert-True ($response.Content -match 'DNS Record Manager|DNS Manager') "Root page did not contain expected UI text."
}

Run-Test "GET /api/zones returns zones" {
    $zones = @(Invoke-ApiRequest -Method GET -Url "$normalizedBaseUrl/api/zones")
    Assert-True ($zones.Count -gt 0) "/api/zones returned no zones."
    Assert-True (($zones | Where-Object { $_.Name -eq $Zone }).Count -gt 0) "Zone '$Zone' was not present in /api/zones response."
}

Run-Test "GET /api/zones/{zone}/records returns records payload" {
    $records = @(Invoke-ApiRequest -Method GET -Url "$normalizedBaseUrl/api/zones/$Zone/records")
    Assert-True ($null -ne $records) "/api/zones/$Zone/records returned null."
    foreach ($record in $records | Select-Object -First 10) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($record.ZoneName)) "A returned record was missing ZoneName."
        Assert-True (-not [string]::IsNullOrWhiteSpace($record.RecordType.ToString())) "A returned record was missing RecordType."
        Assert-True ($record.PSObject.Properties['Data'] -ne $null) "A returned record was missing Data."
    }
}

if ($RunWriteTests) {
    Write-Step "Write checks"

    $request = [pscustomobject]@{
        ZoneName = $Zone
        HostName = $effectiveHostName
        RecordType = 'A'
        Data = $IPv4Address
        TimeToLive = 300
    }

    Run-Test "POST /api/records creates temporary A record" {
        $null = Invoke-ApiRequest -Method POST -Url "$normalizedBaseUrl/api/records" -Body $request
    }

    Run-Test "Created record appears in zone record list" {
        $records = @(Invoke-ApiRequest -Method GET -Url "$normalizedBaseUrl/api/zones/$Zone/records?node=$effectiveHostName")
        $match = $records | Where-Object {
            $_.HostName -eq $effectiveHostName -and
            $_.RecordType.ToString() -eq 'A' -and
            $_.Data -eq $IPv4Address
        } | Select-Object -First 1

        Assert-True ($null -ne $match) "Temporary A record was not found after create."
    }

    Run-Test "DELETE /api/records removes temporary A record" {
        $deleteBody = [pscustomobject]@{
            ZoneName = $Zone
            HostName = $effectiveHostName
            RecordType = 'A'
            Data = $IPv4Address
        }

        $null = Invoke-ApiRequest -Method DELETE -Url "$normalizedBaseUrl/api/records" -Body $deleteBody
    }

    Run-Test "Deleted record no longer appears in zone record list" {
        $records = @(Invoke-ApiRequest -Method GET -Url "$normalizedBaseUrl/api/zones/$Zone/records?node=$effectiveHostName")
        $match = $records | Where-Object {
            $_.HostName -eq $effectiveHostName -and
            $_.RecordType.ToString() -eq 'A' -and
            $_.Data -eq $IPv4Address
        } | Select-Object -First 1

        Assert-True ($null -eq $match) "Temporary A record was still present after delete."
    }
}

Write-Step "Summary"

$passCount = @($results | Where-Object Status -eq 'PASS').Count
$failCount = @($results | Where-Object Status -eq 'FAIL').Count

$results | Format-Table -AutoSize

Write-Host ""
Write-Host ("Passed: {0}" -f $passCount) -ForegroundColor Green
if ($failCount -gt 0) {
    Write-Host ("Failed: {0}" -f $failCount) -ForegroundColor Red
    exit 1
}

Write-Host ("Failed: {0}" -f $failCount) -ForegroundColor Green