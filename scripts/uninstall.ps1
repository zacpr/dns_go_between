#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls DNS Go-Between cleanly.

.DESCRIPTION
    Stops the service, then uninstalls via msiexec using either:
      (a) the path to the original MSI file, or
      (b) the product code found in the Windows registry.
    Logs the msiexec output to %TEMP%\DnsGoBetween-uninstall.log.

.PARAMETER MsiPath
    Path to the original MSI file used to install DNS Go-Between.
    If omitted, the script detects the product code from the registry.

.PARAMETER ProductCode
    Product GUID (e.g. {7B3F1A2E-9C4D-4F8B-A1E3-2D7C8B9F0E1A}).
    If omitted, the script detects it from the registry.

.PARAMETER Silent
    Run fully silently (/qn). Default is basic progress UI (/qb).

.EXAMPLE
    # Auto-detect and uninstall interactively
    .\scripts\uninstall.ps1

    # Uninstall silently using the original MSI
    .\scripts\uninstall.ps1 -MsiPath "C:\dist\DnsGoBetween-1.0.0.msi" -Silent

    # Uninstall using a known product code
    .\scripts\uninstall.ps1 -ProductCode "{7B3F1A2E-9C4D-4F8B-A1E3-2D7C8B9F0E1A}"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $MsiPath,
    [string] $ProductCode,
    [switch] $Silent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName = "DnsGoBetween"
$DisplayName = "DNS Go-Between"
$InstallFolder = "C:\Program Files\DnsGoBetween"
$LogFile     = "$env:TEMP\DnsGoBetween-uninstall.log"
$UiMode      = if ($Silent) { "/qn" } else { "/qb" }

# ── Helpers ───────────────────────────────────────────────────────────────────

function Get-InstalledProductCode {
    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($p in $paths) {
        $entry = Get-ItemProperty $p -ErrorAction SilentlyContinue |
                 Where-Object {
                     $displayNameProperty = $_.PSObject.Properties["DisplayName"]
                     $null -ne $displayNameProperty -and
                     $displayNameProperty.Value -like "*$DisplayName*"
                 } |
                 Select-Object -First 1
        if ($entry) { return $entry.PSChildName }
    }
    return $null
}

function Stop-AppService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    if ($svc.Status -eq 'Running') {
        Write-Host "  Stopping service '$ServiceName'..." -ForegroundColor Yellow
        if ($PSCmdlet.ShouldProcess($ServiceName, "Stop-Service")) {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(15)
            while ((Get-Service $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Stopped') {
                if ((Get-Date) -gt $deadline) {
                    Write-Warning "Service did not stop within 15 s; proceeding anyway."
                    break
                }
                Start-Sleep -Milliseconds 500
            }
        }
        Write-Host "  Service stopped." -ForegroundColor Green
    }
}

function Remove-ServiceRegistration {
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        return
    }

    Write-Host "  Removing lingering service registration..." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess($ServiceName, "sc.exe delete")) {
        sc.exe delete $ServiceName | Out-Null

        $deadline = (Get-Date).AddSeconds(20)
        while (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            if ((Get-Date) -gt $deadline) {
                Write-Warning "Service '$ServiceName' still exists after delete request."
                return
            }

            Start-Sleep -Milliseconds 500
        }

        Write-Host "  Service registration removed." -ForegroundColor Green
    }
}

function Remove-InstallFolderContents {
    if (-not (Test-Path $InstallFolder)) {
        return
    }

    Write-Host "  Removing leftover files from '$InstallFolder'..." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess($InstallFolder, "Remove-Item -Recurse -Force")) {
        Get-ChildItem $InstallFolder -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

        $remaining = Get-ChildItem $InstallFolder -Force -ErrorAction SilentlyContinue
        if ($remaining) {
            Write-Warning "Some files in '$InstallFolder' could not be removed."
        }
        else {
            Write-Host "  Install folder contents removed." -ForegroundColor Green
            try {
                Remove-Item $InstallFolder -Force -ErrorAction SilentlyContinue
            }
            catch {
            }
        }
    }
}

# ── Resolve uninstall target ──────────────────────────────────────────────────

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "  DNS Go-Between Uninstaller"         -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

if (-not $ProductCode -and -not $MsiPath) {
    Write-Host "`nSearching registry for installed product..." -ForegroundColor Yellow
    $ProductCode = Get-InstalledProductCode
    if (-not $ProductCode) {
        Write-Error "$DisplayName does not appear to be installed (no registry entry found under $DisplayName)."
    }
    Write-Host "  Found product code: $ProductCode" -ForegroundColor Green
}

Stop-AppService

# ── Run msiexec /x ────────────────────────────────────────────────────────────

$msiArgs = if ($MsiPath) {
    Write-Host "`nUninstalling via MSI file: $MsiPath" -ForegroundColor Cyan
    @("/x", "`"$MsiPath`"", $UiMode, "/norestart", "/l*v", "`"$LogFile`"")
} else {
    Write-Host "`nUninstalling via product code: $ProductCode" -ForegroundColor Cyan
    @("/x", $ProductCode, $UiMode, "/norestart", "/l*v", "`"$LogFile`"")
}

Write-Host "  Log file: $LogFile" -ForegroundColor DarkGray

if ($PSCmdlet.ShouldProcess($DisplayName, "msiexec /x")) {
    $proc = Start-Process msiexec.exe -ArgumentList $msiArgs -Wait -PassThru
    switch ($proc.ExitCode) {
        0    { Write-Host "`nUninstall succeeded." -ForegroundColor Green }
        3010 {
            Write-Host "`nUninstall succeeded." -ForegroundColor Green
            Write-Host "A system restart is required to complete removal." -ForegroundColor Yellow
        }
        1605 { Write-Warning "Product not found (already uninstalled?). Exit code: 1605." }
        default {
            Write-Warning "msiexec exited with code $($proc.ExitCode)."
            Write-Warning "Check the log for details: $LogFile"
        }
    }
}

Remove-ServiceRegistration

Remove-InstallFolderContents

Write-Host ""
