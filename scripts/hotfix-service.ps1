$ErrorActionPreference = 'Stop'

$sourceCfg = 'c:\dns_go_between\src\DnsGoBetween.Api\appsettings.json'
$targetCfg = 'C:\Program Files\DnsGoBetween\appsettings.json'

Copy-Item -Path $sourceCfg -Destination $targetCfg -Force
Write-Host 'CFG_COPY_OK'

try {
    Start-Service DnsGoBetween -ErrorAction Stop
    Write-Host 'START_OK'
}
catch {
    Write-Host ('START_FAIL: ' + $_.Exception.Message)
}

Get-Service DnsGoBetween | Select-Object Status, Name, StartType

try {
    $resp = Invoke-WebRequest 'http://localhost:6790/health/live' -TimeoutSec 10
    $resp | Select-Object StatusCode, StatusDescription
}
catch {
    Write-Host ('HEALTH_FAIL: ' + $_.Exception.Message)
}
