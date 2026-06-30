# Update-DnsGoBetweenConfig.ps1
# This script is intended to be called by the WiX installer or manually after install
# to update AllowedZones and HTTPS port in appsettings.json.

param(
    [Parameter(Mandatory=$true)]
    [string]$InstallDir,

    [Parameter(Mandatory=$true)]
    [string]$AllowedZonesString,

    [Parameter(Mandatory=$true)]
    [string]$HttpsPort,

    [Parameter(Mandatory=$false)]
    [string]$CustomHostname = "",

    [Parameter(Mandatory=$false)]
    [string]$CertSource = "STORE",

    [Parameter(Mandatory=$false)]
    [string]$CertThumbprint = "",

    [Parameter(Mandatory=$false)]
    [string]$CertPfxPath = "",

    [Parameter(Mandatory=$false)]
    [string]$CertPfxPassword = ""
)

$ErrorActionPreference = "Stop"
$logFile = Join-Path $InstallDir "config_update.log"

function Write-Log($msg) {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    try {
        "[$timestamp] $msg" | Out-File -FilePath $logFile -Append -Encoding UTF8
    }
    catch {
        # Logging must never break install flow.
    }
}

function Get-BestStoreCertificateThumbprint {
    try {
        $machine = $env:COMPUTERNAME
        $domain = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().DomainName
        $fqdn = if ([string]::IsNullOrWhiteSpace($domain)) { $machine } else { "$machine.$domain" }

        $candidates = Get-ChildItem Cert:\LocalMachine\My -ErrorAction Stop |
            Where-Object {
                $_.HasPrivateKey -and
                $_.NotAfter -gt (Get-Date) -and
                (
                    $null -eq $_.EnhancedKeyUsageList -or
                    $_.EnhancedKeyUsageList.Count -eq 0 -or
                    ($_.EnhancedKeyUsageList | Where-Object { $_.FriendlyName -eq 'Server Authentication' -or $_.ObjectId -eq '1.3.6.1.5.5.7.3.1' }).Count -gt 0
                )
            }

        if (-not $candidates) {
            return $null
        }

        $exact = $candidates |
            Where-Object {
                $_.Subject -match "CN=$([regex]::Escape($fqdn))(,|$)" -or
                $_.Subject -match "CN=$([regex]::Escape($machine))(,|$)"
            } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        if ($exact) {
            return ($exact.Thumbprint -replace '\s', '')
        }

        $newest = $candidates | Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($newest) {
            return ($newest.Thumbprint -replace '\s', '')
        }

        return $null
    }
    catch {
        Write-Log "Certificate auto-discovery failed: $($_.Exception.Message)"
        return $null
    }
}

if (-not (Test-Path $InstallDir)) {
    $fallbackDir = Join-Path $env:ProgramData "DnsGoBetween"
    try {
        New-Item -ItemType Directory -Path $fallbackDir -Force | Out-Null
        $logFile = Join-Path $fallbackDir "config_update.log"
    }
    catch {
        $logFile = Join-Path $env:TEMP "DnsGoBetween-config_update.log"
    }
}

try {
    Write-Log "Starting configuration update."
    Write-Log "InstallDir: $InstallDir"
    Write-Log "AllowedZones: $AllowedZonesString"
    Write-Log "HttpsPort: $HttpsPort"
    Write-Log "CustomHostname: $CustomHostname"
    Write-Log "CertSource: $CertSource"
    Write-Log "CertThumbprintProvided: $([string]::IsNullOrWhiteSpace($CertThumbprint) -eq $false)"
    Write-Log "CertPfxPathProvided: $([string]::IsNullOrWhiteSpace($CertPfxPath) -eq $false)"
    Write-Log "CertPfxPasswordProvided: $([string]::IsNullOrWhiteSpace($CertPfxPassword) -eq $false)"

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

    if ($null -eq $json.Tls.Certificate) {
        $json.Tls | Add-Member -MemberType NoteProperty -Name "Certificate" -Value ([pscustomobject]@{})
    }

    if ($null -eq $json.Tls.Certificate.StoreName) { $json.Tls.Certificate.StoreName = "My" }
    if ($null -eq $json.Tls.Certificate.StoreLocation) { $json.Tls.Certificate.StoreLocation = "LocalMachine" }
    if ($null -eq $json.Tls.Certificate.Subject) { $json.Tls.Certificate.Subject = "" }
    if ($json.Tls.PSObject.Properties.Name -contains "AutoSelectMachineCertificate") {
        if ($null -eq $json.Tls.AutoSelectMachineCertificate) {
            $json.Tls.AutoSelectMachineCertificate = $true
        }
    }
    else {
        $json.Tls | Add-Member -MemberType NoteProperty -Name "AutoSelectMachineCertificate" -Value $true
    }

    $json.Tls.HttpsPort = $port
    if ($null -eq $json.Tls.EnableHttp) { $json.Tls.EnableHttp = $false }
    if ($null -eq $json.Tls.HttpPort) { $json.Tls.HttpPort = 0 }
    if ($null -eq $json.Tls.RedirectHttpToHttps) { $json.Tls.RedirectHttpToHttps = $false }

    $certSourceNormalized = if ([string]::IsNullOrWhiteSpace($CertSource)) {
        "STORE"
    }
    else {
        $CertSource.Trim().ToUpperInvariant()
    }

    $normalizedCustomHostname = if ([string]::IsNullOrWhiteSpace($CustomHostname)) {
        ""
    }
    else {
        $CustomHostname.Trim().ToLowerInvariant()
    }
    $hasCustomHostname = -not [string]::IsNullOrWhiteSpace($normalizedCustomHostname)

    if ($hasCustomHostname) {
        $json.Tls.Certificate.Subject = $normalizedCustomHostname
        $json.Tls.AutoSelectMachineCertificate = $false
        Write-Log "Custom hostname configured. Machine certificate auto-selection disabled."
    }
    else {
        $json.Tls.AutoSelectMachineCertificate = $true
    }

    switch ($certSourceNormalized) {
        "PFX" {
            $trimmedPfxPath = $CertPfxPath.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmedPfxPath) -and (Test-Path $trimmedPfxPath)) {
                $json.Tls.Certificate.PfxPath = $trimmedPfxPath
                $json.Tls.Certificate.PfxPassword = $CertPfxPassword
                $json.Tls.Certificate.Thumbprint = ""
                Write-Log "Configured TLS certificate source: PFX"
            }
            else {
                Write-Log "PFX mode selected but file path is missing or not found."
                $json.Tls.Certificate.PfxPath = ""
                $json.Tls.Certificate.PfxPassword = ""
                if ($hasCustomHostname) {
                    $json.Tls.Certificate.Thumbprint = ""
                    Write-Log "Custom hostname is set, so no fallback auto certificate selection was performed."
                }
                else {
                    Write-Log "Falling back to store lookup."
                    $fallbackThumbprint = Get-BestStoreCertificateThumbprint
                    $json.Tls.Certificate.Thumbprint = if ($fallbackThumbprint) { $fallbackThumbprint } else { "" }
                    if ($fallbackThumbprint) {
                        Write-Log "Fallback store certificate selected by thumbprint."
                    }
                    else {
                        Write-Log "No fallback store certificate found. Service will run HTTP on primary port."
                    }
                }
            }
        }
        default {
            $thumbprint = $CertThumbprint.Replace(" ", "")
            if ([string]::IsNullOrWhiteSpace($thumbprint)) {
                if ($hasCustomHostname) {
                    Write-Log "Custom hostname is set, so STORE mode requires explicit thumbprint or matching certificate subject."
                }
                else {
                    $thumbprint = Get-BestStoreCertificateThumbprint
                    if ($thumbprint) {
                        Write-Log "Auto-selected LocalMachine\\My certificate by thumbprint for STORE mode."
                    }
                    else {
                        Write-Log "STORE mode selected with no thumbprint and no suitable auto-selected certificate."
                    }
                }
            }

            $json.Tls.Certificate.Thumbprint = if ($thumbprint) { $thumbprint } else { "" }
            $json.Tls.Certificate.PfxPath = ""
            $json.Tls.Certificate.PfxPassword = ""
            Write-Log "Configured TLS certificate source: STORE"
        }
    }

    $hasTlsMaterial =
        (-not [string]::IsNullOrWhiteSpace($json.Tls.Certificate.Thumbprint)) -or
        (-not [string]::IsNullOrWhiteSpace($json.Tls.Certificate.PfxPath))

    # Redirect only when concrete TLS material is configured.
    $json.Tls.RedirectHttpToHttps = $hasTlsMaterial
    Write-Log "RedirectHttpToHttps set to: $($json.Tls.RedirectHttpToHttps)"

    $json | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8 -Force
    Write-Log "Successfully updated appsettings.json"
}
catch {
    Write-Log "EXCEPTION: $($_.Exception.Message)"
    Write-Log "STACK: $($_.ScriptStackTrace)"
    # Do not fail MSI install for non-critical config update issues.
    exit 0
}
