#!/usr/bin/env pwsh
# Quick install test with minimal overhead

$msi = "c:\dns_go_between\dist\DnsGoBetween-1.0.0.msi"

# First uninstall if present
Write-Host "Checking for existing service..." -ForegroundColor Gray
$svc = Get-Service DnsGoBetween -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Uninstalling..."
    msiexec /x "$msi" /qn /norestart
    Start-Sleep 5
    Stop-Process dotnet -Force -ErrorAction SilentlyContinue
}

Write-Host "Installing MSI..." -ForegroundColor Cyan
msiexec /i "$msi" /qn /norestart

Write-Host "Waiting for install..." -ForegroundColor Gray
Start-Sleep 5

Write-Host "Checking service..." -ForegroundColor Cyan
$svc = Get-Service DnsGoBetween -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "✓ Service installed" -ForegroundColor Green
    Write-Host "  Name:   $($svc.Name)"
    Write-Host "  Status: $($svc.Status)"
    Write-Host "  Start:  $($svc.StartType)"
} else {
    Write-Host "✗ Service NOT found" -ForegroundColor Red
}
