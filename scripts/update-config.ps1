# Update-DnsGoBetweenConfig.ps1
# This script is intended to be called by the WiX installer or manually after install
# to update the AllowedZones in appsettings.json.

param(
    [Parameter(Mandatory=$true)]
    [string]$InstallDir,

    [Parameter(Mandatory=$true)]
    [string]$AllowedZonesString
)

$ErrorActionPreference = "Stop"
$logFile = Join-Path $InstallDir "config_update.log"

function Write-Log($msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$timestamp] $msg" | Out-File -FilePath $logFile -Append
}

Write-Log "Starting configuration update."
Write-Log "InstallDir: $InstallDir"
Write-Log "AllowedZones: $AllowedZonesString"

try {
    $configPath = Join-Path $InstallDir "appsettings.json"
    if (-not (Test-Path $configPath)) {
        Write-Log "ERROR: appsettings.json not found at $configPath"
        exit 1
    }

    $json = Get-Content $configPath -Raw | ConvertFrom-Json
    
    # Split the comma-separated string into an array
    $zonesArray = $AllowedZonesString.Split(",;", [System.StringSplitOptions]::RemoveEmptyEntries).Trim()
    
    # Update the object
    if ($null -eq $json.Dns) {
        Write-Log "Initializing Dns section in config"
        $json | Add-Member -MemberType NoteProperty -Name "Dns" -Value @{ "AllowedZones" = $zonesArray }
    } else {
        $json.Dns.AllowedZones = $zonesArray
    }

    # Save it back
    $json | ConvertTo-Json -Depth 20 | Set-Content $configPath
    Write-Log "Successfully updated appsettings.json"
} catch {
    Write-Log "EXCEPTION: $($_.Exception.Message)"
    Write-Log "STACK: $($_.ScriptStackTrace)"
    exit 1
} 
