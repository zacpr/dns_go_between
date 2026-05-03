# Update-DnsGoBetweenConfig.ps1
# This script is intended to be called by the WiX installer or manually after install
# to update AllowedZones and HTTPS port in appsettings.json.

param(
    [Parameter(Mandatory=$true)]
    [string]$InstallDir,

    [Parameter(Mandatory=$true)]
    [string]$AllowedZonesString,

    [Parameter(Mandatory=$true)]
    [string]$HttpsPort
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
Write-Log "HttpsPort: $HttpsPort"

try {
    $port = 0
    if (-not [int]::TryParse($HttpsPort, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw "Invalid HttpsPort '$HttpsPort'. Expected integer in range 1-65535."
    }

    $configPath = Join-Path $InstallDir "appsettings.json"
    if (-not (Test-Path $configPath)) {
        Write-Log "ERROR: appsettings.json not found at $configPath"
        exit 1
    }

    $json = Get-Content $configPath -Raw | ConvertFrom-Json

    # Split comma/semicolon lists safely and trim each entry.
    $zonesArray = @()
    if (-not [string]::IsNullOrWhiteSpace($AllowedZonesString)) {
        $zonesArray = $AllowedZonesString -split '[,;]'
        $zonesArray = $zonesArray |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    if ($null -eq $json.Dns) {
        Write-Log "Initializing Dns section in config"
        $json | Add-Member -MemberType NoteProperty -Name "Dns" -Value ([pscustomobject]@{})
    }
    $json.Dns.AllowedZones = $zonesArray

    if ($null -eq $json.Tls) {
        $json | Add-Member -MemberType NoteProperty -Name "Tls" -Value ([pscustomobject]@{})
    }

    $json.Tls.HttpsPort = $port
    if ($null -eq $json.Tls.EnableHttp) { $json.Tls.EnableHttp = $false }
    if ($null -eq $json.Tls.HttpPort) { $json.Tls.HttpPort = 0 }

    $json | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8 -Force
    Write-Log "Successfully updated appsettings.json"
}
catch {
    Write-Log "EXCEPTION: $($_.Exception.Message)"
    Write-Log "STACK: $($_.ScriptStackTrace)"
    exit 1
}
