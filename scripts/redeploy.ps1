#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Full redeploy of DNS Go-Between: uninstall if present, build MSI, install, and verify.

.PARAMETER Version
    Version number to embed in the MSI. Defaults to 1.0.11.

.PARAMETER SkipUninstall
    Skip the uninstall step.

.PARAMETER SkipBuild
    Skip the build step and use the latest MSI from .\dist\.

.PARAMETER SyncSourceConfig
    After install, copy src\DnsGoBetween.Api\appsettings.json over the installed appsettings.json.
    Off by default so verification checks the actual MSI contents.

.EXAMPLE
    .\scripts\redeploy.ps1

.EXAMPLE
    .\scripts\redeploy.ps1 -Version 1.1.0

.EXAMPLE
    .\scripts\redeploy.ps1 -SkipUninstall

.EXAMPLE
    .\scripts\redeploy.ps1 -SyncSourceConfig
#>
[CmdletBinding()]
param(
    [string] $Version = "1.0.11",
    [switch] $SkipUninstall,
    [switch] $SkipBuild,
    [switch] $SyncSourceConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName = "DnsGoBetween"
$DisplayName = "DNS Go-Between"
$InstallFolder = "C:\Program Files\DnsGoBetween"
$Root = Split-Path $PSScriptRoot -Parent
$DistDir = Join-Path $Root "dist"
$PublishDir = Join-Path $env:TEMP "DnsGoBetween\publish"
$HealthUrl = "http://localhost:6790/health/live"
$ReadyUrl = "http://localhost:6790/health/ready"

function Write-Step([string]$Text) {
    Write-Host "`n$Text" -ForegroundColor Cyan
    Write-Host ("-" * $Text.Length) -ForegroundColor DarkCyan
}

function Write-Ok([string]$Text) {
    Write-Host "  [OK]  $Text" -ForegroundColor Green
}

function Write-WarnLine([string]$Text) {
    Write-Host "  [!!]  $Text" -ForegroundColor Yellow
}

function Write-Fail([string]$Text) {
    Write-Host "  [XX]  $Text" -ForegroundColor Red
}

function Fail-Run([string]$Text) {
    Write-Fail $Text
    exit 1
}

function Get-InstalledProductCode {
    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    foreach ($path in $paths) {
        $entry = Get-ItemProperty $path -ErrorAction SilentlyContinue |
            Where-Object {
                $displayNameProperty = $_.PSObject.Properties["DisplayName"]
                $null -ne $displayNameProperty -and
                $displayNameProperty.Value -like "*$DisplayName*"
            } |
            Select-Object -First 1

        if ($entry) {
            return $entry.PSChildName
        }
    }

    return $null
}

function Stop-InstalledService {
    $service = Get-Service $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        return
    }

    if ($service.Status -eq "Running") {
        Write-Host "  Stopping service..." -ForegroundColor Yellow
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue

        $deadline = (Get-Date).AddSeconds(15)
        while ($true) {
            $current = Get-Service $ServiceName -ErrorAction SilentlyContinue
            if (-not $current -or $current.Status -eq "Stopped") {
                break
            }

            if ((Get-Date) -gt $deadline) {
                Write-WarnLine "Service did not stop within 15 seconds."
                break
            }

            Start-Sleep -Milliseconds 500
        }

        Write-Ok "Service stopped."
    }
}

function Remove-ServiceRegistration {
    if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        return
    }

    Write-WarnLine "Removing lingering service registration."
    sc.exe delete $ServiceName | Out-Null

    $deadline = (Get-Date).AddSeconds(20)
    while (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) {
            Fail-Run "Service '$ServiceName' still exists after delete request."
        }

        Start-Sleep -Milliseconds 500
    }

    Write-Ok "Service registration removed."
}

function Remove-InstallFolderContents {
    if (-not (Test-Path $InstallFolder)) {
        return
    }

    Write-WarnLine "Removing leftover files from install folder."
    Get-ChildItem $InstallFolder -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    $remaining = Get-ChildItem $InstallFolder -Force -ErrorAction SilentlyContinue
    if ($remaining) {
        Fail-Run "Some files in '$InstallFolder' could not be removed during cleanup."
    }

    try {
        Remove-Item $InstallFolder -Force -ErrorAction SilentlyContinue
    }
    catch {
    }

    Write-Ok "Install folder cleaned."
}

function Get-LatestMsiPath {
    $latest = Get-ChildItem $DistDir -Filter "*.msi" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($latest) {
        return $latest.FullName
    }

    return $null
}

function Get-FileSha256([string]$Path) {
    if (-not (Test-Path $Path)) {
        return $null
    }

    return (Get-FileHash $Path -Algorithm SHA256).Hash
}

function Sync-PublishedOutputToInstall {
    if (-not (Test-Path $PublishDir) -or -not (Test-Path $InstallFolder)) {
        return
    }

    $publishInfrastructure = Join-Path $PublishDir "DnsGoBetween.Infrastructure.dll"
    $installedInfrastructure = Join-Path $InstallFolder "DnsGoBetween.Infrastructure.dll"
    $publishApi = Join-Path $PublishDir "DnsGoBetween.Api.dll"
    $installedApi = Join-Path $InstallFolder "DnsGoBetween.Api.dll"

    $publishInfrastructureHash = Get-FileSha256 $publishInfrastructure
    $installedInfrastructureHash = Get-FileSha256 $installedInfrastructure
    $publishApiHash = Get-FileSha256 $publishApi
    $installedApiHash = Get-FileSha256 $installedApi

    $needsSync =
        ($null -ne $publishInfrastructureHash -and $publishInfrastructureHash -ne $installedInfrastructureHash) -or
        ($null -ne $publishApiHash -and $publishApiHash -ne $installedApiHash)

    if (-not $needsSync) {
        Write-Ok "Installed binaries match publish output."
        return
    }

    Write-WarnLine "Installed binaries do not match publish output. Syncing published files into install folder."
    Copy-Item (Join-Path $PublishDir "*") $InstallFolder -Recurse -Force
    Write-Ok "Published files synced into install folder."
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  DNS Go-Between Full Redeploy" -ForegroundColor Cyan
Write-Host "  Version : $Version" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

Write-Step "STEP 1/4 - Uninstall if present"

if ($SkipUninstall) {
    Write-WarnLine "Skipped because SkipUninstall was set."
}
else {
    Stop-InstalledService

    $productCode = Get-InstalledProductCode
    if (-not $productCode) {
        Write-WarnLine "$DisplayName not found in registry. Skipping uninstall."
    }
    else {
        Write-Host "  Found product code: $productCode" -ForegroundColor DarkGray
        $uninstallLog = Join-Path $env:TEMP "DnsGoBetween-uninstall.log"
        $uninstallArgs = @(
            "/x",
            $productCode,
            "/qn",
            "/norestart",
            "/l*v",
            $uninstallLog
        )

        $proc = Start-Process msiexec.exe -ArgumentList $uninstallArgs -Wait -PassThru
        switch ($proc.ExitCode) {
            0 { Write-Ok "Uninstall succeeded." }
            3010 { Write-Ok "Uninstall succeeded. Reboot pending, continuing." }
            1605 { Write-WarnLine "Product already removed (msiexec 1605)." }
            default {
                Write-Fail "msiexec /x exited $($proc.ExitCode). Check $uninstallLog"
                exit 1
            }
        }

        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Remove-ServiceRegistration
        }

        Remove-InstallFolderContents
    }
}

Write-Step "STEP 2/4 - Build MSI"

if ($SkipBuild) {
    Write-WarnLine "Skipped because SkipBuild was set."
    $msiPath = Get-LatestMsiPath
    if (-not $msiPath) {
        throw "No MSI found in $DistDir while SkipBuild is set."
    }

    Write-Ok "Using existing MSI: $msiPath"
}
else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $defaultDotnet = "C:\Program Files\dotnet"
        if (Test-Path (Join-Path $defaultDotnet "dotnet.exe")) {
            $env:PATH = "$defaultDotnet;$env:PATH"
        }
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet CLI not found. Install .NET 8 SDK and retry."
    }

    $buildScript = Join-Path $PSScriptRoot "build-installer.ps1"
    & $buildScript -Version $Version
    if ($LASTEXITCODE -ne 0) {
        throw "build-installer.ps1 failed with exit code $LASTEXITCODE."
    }

    $msiPath = Join-Path $DistDir "DnsGoBetween-$Version.msi"
    if (-not (Test-Path $msiPath)) {
        $msiPath = Get-LatestMsiPath
    }

    if (-not $msiPath) {
        throw "Build completed but no MSI was found in $DistDir."
    }

    Write-Ok "MSI built: $msiPath"
}

Write-Step "STEP 3/4 - Install MSI"

$installLog = Join-Path $env:TEMP ("DnsGoBetween-install-{0}.log" -f $Version)
Write-Host "  MSI : $msiPath" -ForegroundColor DarkGray
Write-Host "  Log : $installLog" -ForegroundColor DarkGray

$installArgs = @(
    "/i",
    $msiPath,
    "/qn",
    "/norestart",
    "/l*v",
    $installLog
)

$proc = Start-Process msiexec.exe -ArgumentList $installArgs -Wait -PassThru
switch ($proc.ExitCode) {
    0 { Write-Ok "Install succeeded." }
    3010 { Write-Ok "Install succeeded. Reboot pending, service should still start." }
    default {
        Write-Fail "msiexec /i exited $($proc.ExitCode). Check $installLog"
        Write-Host "`nLast 30 lines of install log:" -ForegroundColor Yellow
        Get-Content $installLog -ErrorAction SilentlyContinue | Select-Object -Last 30
        exit 1
    }
}

Write-Step "STEP 4/4 - Start service and verify"

Sync-PublishedOutputToInstall

$srcSettings = Join-Path $Root "src\DnsGoBetween.Api\appsettings.json"
$dstSettings = Join-Path $InstallFolder "appsettings.json"
if ($SyncSourceConfig -and (Test-Path $srcSettings) -and (Test-Path $InstallFolder)) {
    Copy-Item $srcSettings $dstSettings -Force
    Write-Ok "appsettings.json synced from source."
}
elseif ($SyncSourceConfig) {
    Write-WarnLine "SyncSourceConfig was set, but source or installed appsettings.json was not found."
}

$service = Get-Service $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Fail "Service '$ServiceName' not found after install."
    exit 1
}

if ($service.Status -ne "Running") {
    Write-Host "  Starting service '$ServiceName'..." -ForegroundColor Yellow
    Start-Service $ServiceName -ErrorAction Stop
}

$deadline = (Get-Date).AddSeconds(20)
while ($true) {
    $status = (Get-Service $ServiceName).Status
    if ($status -eq "Running") {
        break
    }

    if ((Get-Date) -gt $deadline) {
        Write-Fail "Service did not reach Running within 20 seconds. Current status: $status"
        Write-Host "`nRecent Application log events:" -ForegroundColor Yellow
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'DnsGoBetween|dotnet' } |
            Select-Object -First 10 TimeCreated, LevelDisplayName, Message | Format-List
        exit 1
    }

    Start-Sleep -Milliseconds 600
}

Write-Ok "Service is Running."

Write-Host "  Waiting for listener on port 6790..." -ForegroundColor Yellow
$deadline = (Get-Date).AddSeconds(20)
$listening = $false
while ((Get-Date) -lt $deadline) {
    $connection = Get-NetTCPConnection -LocalPort 6790 -State Listen -ErrorAction SilentlyContinue
    if ($connection) {
        $listening = $true
        break
    }

    Start-Sleep -Milliseconds 600
}

if ($listening) {
    Write-Ok "Port 6790 is listening."
}
else {
    Write-WarnLine "Port 6790 is not listening yet. Health checks may fail."
}

try {
    $response = Invoke-WebRequest $HealthUrl -TimeoutSec 12 -UseBasicParsing
    Write-Ok "/health/live -> HTTP $($response.StatusCode)"
}
catch {
    Fail-Run "/health/live failed: $($_.Exception.Message)"
}

try {
    $response = Invoke-WebRequest $ReadyUrl -TimeoutSec 12 -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        Write-Ok "/health/ready -> HTTP 200 (DNS cmdlets ready)"
    }
    else {
        Fail-Run "/health/ready returned HTTP $($response.StatusCode). Body: $($response.Content)"
    }
}
catch {
    if ($_.Exception.Response) {
        $httpResponse = $_.Exception.Response
        $statusCode = [int]$httpResponse.StatusCode
        $body = ""

        try {
            $stream = $httpResponse.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try {
                    $body = $reader.ReadToEnd()
                }
                finally {
                    $reader.Dispose()
                }
            }
        }
        catch {
        }

        Fail-Run "/health/ready returned HTTP $statusCode. Body: $body"
    }

    Fail-Run "/health/ready failed: $($_.Exception.Message)"
}

Write-Host "============================================" -ForegroundColor Green

Write-Step "STEP 5/5 - Post-install smoke tests"

$smokeTest = Join-Path $PSScriptRoot "smoke-test.ps1"
if (Test-Path $smokeTest) {
    Write-Host "  Running API smoke tests..." -ForegroundColor Yellow
    & $smokeTest -Zone "ashurtech.net"
    if ($LASTEXITCODE -ne 0) {
        Fail-Run "Smoke tests failed with exit code $LASTEXITCODE."
    }
    Write-Ok "Smoke tests passed."
}
else {
    Write-WarnLine "smoke-test.ps1 not found, skipping."
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  Redeploy complete" -ForegroundColor Green
Write-Host "  Version : $Version" -ForegroundColor Green
Write-Host "  MSI     : $msiPath" -ForegroundColor Green
Write-Host "  Service : $((Get-Service $ServiceName -ErrorAction SilentlyContinue).Status)" -ForegroundColor Green
Write-Host "  UI      : http://localhost:6790/" -ForegroundColor Green
Write-Host "  Swagger : http://localhost:6790/swagger/index.html" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
