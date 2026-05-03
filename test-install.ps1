#!/usr/bin/env pwsh
$msi = "c:\dns_go_between\dist\DnsGoBetween-1.0.0.msi"
$log = "$env:TEMP\install.log"

Write-Host "Installing MSI: $msi" -ForegroundColor Cyan
Write-Host "Log file: $log" -ForegroundColor Gray

# Uninstall first if it exists
$svc = Get-Service DnsGoBetween -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Service already exists. Uninstalling first..."
    msiexec /x "$msi" /qb /l*vx "$env:TEMP\uninstall.log"
    Start-Sleep -Seconds 3
}

# Install
$result = Start-Process msiexec.exe -ArgumentList "/i", "`"$msi`"", "/qb", "/l*vx", "`"$log`"" -Wait -PassThru
Write-Host "Install exit code: $($result.ExitCode)" -ForegroundColor Green

Start-Sleep -Seconds 3

Write-Host "`nChecking service status..." -ForegroundColor Cyan
$svc = Get-Service DnsGoBetween -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Service found!" -ForegroundColor Green
    $svc | Format-Table Name, Status, StartType
    Write-Host "`nService details:"
    $svc | Get-ServiceDetail -ErrorAction SilentlyContinue
} else {
    Write-Host "Service NOT found" -ForegroundColor Red
}

Write-Host "`nInstall log (last 50 lines):" -ForegroundColor Cyan
if (Test-Path $log) {
    Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Last 50
} else {
    Write-Host "Log file not found"
}
