#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the DNS Go-Between MSI installer.

.DESCRIPTION
    1. Checks / installs prerequisites (dotnet SDK, WiX v4 dotnet tool).
    2. Publishes DnsGoBetween.Api (framework-dependent, win-x64).
    3. Builds the WiX installer project into an MSI under .\dist\.

.PARAMETER Version
    Semantic version embedded in the MSI and used as the output filename suffix.
    Defaults to 1.0.0.

.PARAMETER Configuration
    Build configuration: Release (default) or Debug.

.PARAMETER SkipPublish
    Skip the dotnet publish step (use an existing .\publish\ directory).

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -Version 1.2.0
    .\scripts\build-installer.ps1 -Version 1.2.0 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]  $Version       = "1.0.0",
    [ValidateSet("Release", "Debug")]
    [string]  $Configuration = "Release",
    [switch]  $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Paths ─────────────────────────────────────────────────────────────────────

$Root          = Split-Path $PSScriptRoot -Parent
$ApiProject    = Join-Path $Root "src\DnsGoBetween.Api\DnsGoBetween.Api.csproj"
$InstallerProj = Join-Path $Root "installer\DnsGoBetween.Installer\DnsGoBetween.Installer.wixproj"
$PublishDir    = Join-Path $env:TEMP "DnsGoBetween\publish"
$InstallerDir  = Join-Path $Root "installer\DnsGoBetween.Installer"
$FinalHarvestWxs = Join-Path $InstallerDir "HarvestedFiles.wxs"
$DistDir       = Join-Path $Root "dist"
$InstallerObjDir = Join-Path $env:TEMP ("DnsGoBetween\installer-obj-{0}-{1}" -f $Version, $PID)
$MsiName       = "DnsGoBetween-$Version.msi"
$MsiPath       = Join-Path $DistDir $MsiName

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan

Write-Host "  DNS Go-Between Installer Build"        -ForegroundColor Cyan
Write-Host "  Version : $Version"                   -ForegroundColor Cyan
Write-Host "  Config  : $Configuration"             -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

New-Item -Path $InstallerObjDir -ItemType Directory -Force | Out-Null

# ── Prerequisites ─────────────────────────────────────────────────────────────

function Test-Command([string]$Name) {
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

Write-Host "`n[prereq] Checking .NET SDK..." -ForegroundColor Yellow
# Add the default dotnet install path if dotnet is not yet on PATH
if (-not (Test-Command "dotnet")) {
    $defaultDotnet = "C:\Program Files\dotnet"
    if (Test-Path (Join-Path $defaultDotnet "dotnet.exe")) {
        $env:PATH = "$defaultDotnet;$env:PATH"
    }
}     
if (-not (Test-Command "dotnet")) {
    Write-Error @"
dotnet CLI not found in PATH.
Install the .NET 8 SDK from: https://dotnet.microsoft.com/download
After installing, open a new terminal and retry.
"@
}
$sdkVersion = (& dotnet --version 2>&1)
Write-Host "         dotnet $sdkVersion OK" -ForegroundColor Green

Write-Host "[prereq] Checking WiX v4 tool..." -ForegroundColor Yellow
Write-Host "         WixToolset.Sdk resolved by dotnet build (NuGet)" -ForegroundColor Green

function New-HarvestedFilesWxs {
    param(
        [Parameter(Mandatory)] [string] $SourceDir,
        [Parameter(Mandatory)] [string] $OutputFile
    )

    function Get-RelativePath {
        param(
            [Parameter(Mandatory)] [string] $BasePath,
            [Parameter(Mandatory)] [string] $TargetPath
        )

        $baseFullPath = [IO.Path]::GetFullPath($BasePath)
        if (-not $baseFullPath.EndsWith([IO.Path]::DirectorySeparatorChar)) {
            $baseFullPath += [IO.Path]::DirectorySeparatorChar
        }

        $targetFullPath = [IO.Path]::GetFullPath($TargetPath)
        $baseUri = [Uri]$baseFullPath
        $targetUri = [Uri]$targetFullPath

        return [Uri]::UnescapeDataString(
            $baseUri.MakeRelativeUri($targetUri).ToString().Replace('/', [IO.Path]::DirectorySeparatorChar)
        )
    }

    if (-not (Test-Path $SourceDir)) {
        throw "Publish directory not found: $SourceDir"
    }

    $allFiles = @(Get-ChildItem $SourceDir -Recurse -File |
        Where-Object { $_.Name -ne 'DnsGoBetween.Api.exe' } |
        Sort-Object FullName)

    if ($allFiles.Count -eq 0) {
        throw "No publish files found to harvest in: $SourceDir"
    }

    $directoryIdByRelativePath = @{}
    $directoryTree = @{}
    $nextDir = 1

    foreach ($file in $allFiles) {
        $relativePath = Get-RelativePath -BasePath $SourceDir -TargetPath $file.FullName
        $relativeDir = [IO.Path]::GetDirectoryName($relativePath)
        if ([string]::IsNullOrEmpty($relativeDir)) { continue }

        $parts = $relativeDir -split '[\\/]'
        $acc = ""
        foreach ($part in $parts) {
            $acc = if ($acc) { "$acc\$part" } else { $part }
            if (-not $directoryIdByRelativePath.ContainsKey($acc)) {
                $directoryIdByRelativePath[$acc] = ('DIR_{0:D4}' -f $nextDir)
                $directoryTree[$acc] = @{
                    Id = $directoryIdByRelativePath[$acc]
                    Name = $part
                    Children = New-Object System.Collections.Generic.List[string]
                }

                $parentPath = [IO.Path]::GetDirectoryName($acc)
                if (-not [string]::IsNullOrEmpty($parentPath) -and $directoryTree.ContainsKey($parentPath)) {
                    $directoryTree[$parentPath].Children.Add($acc)
                }

                $nextDir++
            }
        }
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')

    function Write-DirectoryNode {
        param(
            [Parameter(Mandatory)] [string] $Path,
            [Parameter(Mandatory)] [int] $Indent
        )

        $node = $directoryTree[$Path]
        $pad = ' ' * $Indent
        $nameEscaped = [System.Security.SecurityElement]::Escape($node.Name)

        if ($node.Children.Count -eq 0) {
            [void]$sb.AppendLine(('{0}<Directory Id="{1}" Name="{2}" />' -f $pad, $node.Id, $nameEscaped))
            return
        }

        [void]$sb.AppendLine(('{0}<Directory Id="{1}" Name="{2}">' -f $pad, $node.Id, $nameEscaped))
        foreach ($childPath in ($node.Children | Sort-Object)) {
            Write-DirectoryNode -Path $childPath -Indent ($Indent + 2)
        }
        [void]$sb.AppendLine('{0}</Directory>' -f $pad)
    }

    if ($directoryIdByRelativePath.Count -gt 0) {
        [void]$sb.AppendLine('  <Fragment>')
        [void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

        $rootPaths = $directoryIdByRelativePath.Keys |
            Where-Object { [string]::IsNullOrEmpty([IO.Path]::GetDirectoryName($_)) } |
            Sort-Object

        foreach ($rootPath in $rootPaths) {
            Write-DirectoryNode -Path $rootPath -Indent 6
        }

        [void]$sb.AppendLine('    </DirectoryRef>')
        [void]$sb.AppendLine('  </Fragment>')
    }

    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <ComponentGroup Id="HarvestedFiles">')

    $index = 1
    foreach ($file in $allFiles) {
        $componentId = 'CMP_{0:D4}' -f $index
        $fileId = 'FIL_{0:D4}' -f $index
        $relativePath = Get-RelativePath -BasePath $SourceDir -TargetPath $file.FullName
        $relativeDir = [IO.Path]::GetDirectoryName($relativePath)
        $directoryId = if ([string]::IsNullOrEmpty($relativeDir)) { 'INSTALLFOLDER' } else { $directoryIdByRelativePath[$relativeDir] }
        $sourceEscaped = [System.Security.SecurityElement]::Escape($file.FullName)

        [void]$sb.AppendLine(('      <Component Id="{0}" Guid="*" Directory="{1}">' -f $componentId, $directoryId))
        [void]$sb.AppendLine(('        <File Id="{0}" Source="{1}" KeyPath="yes" />' -f $fileId, $sourceEscaped))
        [void]$sb.AppendLine('      </Component>')

        $index++
    }

    [void]$sb.AppendLine('    </ComponentGroup>')
    [void]$sb.AppendLine('  </Fragment>')
    [void]$sb.AppendLine('</Wix>')

    [IO.File]::WriteAllText($OutputFile, $sb.ToString(), [Text.UTF8Encoding]::new($false))
}

# ── Step 1: dotnet publish ────────────────────────────────────────────────────

if (-not $SkipPublish) {
    Write-Host "`n[1/2] Publishing $Configuration build..." -ForegroundColor Cyan
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
    New-Item $PublishDir -ItemType Directory -Force | Out-Null

    & dotnet publish $ApiProject `
        --configuration $Configuration `
        --output $PublishDir `
        --self-contained false `
        --runtime win-x64 `
        /p:Version=$Version

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    Write-Host "       Published to: $PublishDir" -ForegroundColor Green
} else {
    Write-Host "`n[1/2] Skipping publish (using existing $PublishDir)" -ForegroundColor DarkGray
    if (-not (Test-Path $PublishDir)) {
        throw "-SkipPublish was specified but '$PublishDir' does not exist. Run without -SkipPublish first."
    }
}

# Ensure installer defaults are HTTPS-first in publish output.
$publishAppSettings = Join-Path $PublishDir "appsettings.json"
if (Test-Path $publishAppSettings) {
    try {
        $cfg = Get-Content $publishAppSettings -Raw | ConvertFrom-Json

        if ($null -eq $cfg.Tls) {
            $cfg | Add-Member -MemberType NoteProperty -Name Tls -Value ([pscustomobject]@{})
        }
        if ($null -eq $cfg.Tls.HttpsPort) { $cfg.Tls.HttpsPort = 6790 }
        if ($null -eq $cfg.Tls.EnableHttp) { $cfg.Tls.EnableHttp = $false }
        if ($null -eq $cfg.Tls.HttpPort) { $cfg.Tls.HttpPort = 0 }

        $cfg | ConvertTo-Json -Depth 20 | Set-Content -Path $publishAppSettings -Encoding UTF8
        Write-Host "       Verified publish appsettings TLS defaults." -ForegroundColor Green
    }
    catch {
        throw "Failed to normalize publish appsettings at '$publishAppSettings': $($_.Exception.Message)"
    }
}
# ── Step 2: Build MSI ─────────────────────────────────────────────────────────

Write-Host "`n[2/2] Building MSI with WiX v4..." -ForegroundColor Cyan
New-Item $DistDir -ItemType Directory -Force | Out-Null

Write-Host "       Harvesting publish output to HarvestedFiles.wxs..." -ForegroundColor Cyan
New-HarvestedFilesWxs -SourceDir $PublishDir -OutputFile $FinalHarvestWxs
Write-Host "       Harvested file list written: $FinalHarvestWxs" -ForegroundColor Green

& dotnet build $InstallerProj `
    --configuration Release `
    -p:Version=$Version `
    "-p:PublishDir=$PublishDir\" `
    "-p:OutputPath=$DistDir\" `
    "-p:MSBuildProjectExtensionsPath=$InstallerObjDir\" `
    -p:Platform=x64

if ($LASTEXITCODE -ne 0) { throw "WiX build failed (exit $LASTEXITCODE)." }

# WiX outputs <OutputName>-<version>.msi; rename if needed
$builtMsi = Get-ChildItem $DistDir -Filter "*.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($builtMsi -and $builtMsi.Name -ne $MsiName) {
    Rename-Item $builtMsi.FullName $MsiPath -Force
}

if (-not (Test-Path $MsiPath)) {
    $found = Get-ChildItem $DistDir -Filter "*.msi" | Select-Object -First 1
    if ($found) { $MsiPath = $found.FullName }
    else { throw "MSI not found in $DistDir after successful build." }
}

$MsiSize = [math]::Round((Get-Item $MsiPath).Length / 1MB, 1)

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Build complete!" -ForegroundColor Green
Write-Host "  MSI  : $MsiPath" -ForegroundColor Green
Write-Host "  Size : $($MsiSize) MB" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Install (interactive):"    -ForegroundColor White
Write-Host "  msiexec /i `"$MsiPath`"" -ForegroundColor Gray
Write-Host ""
Write-Host "Install (silent, no reboot prompt):" -ForegroundColor White
Write-Host "  msiexec /i `"$MsiPath`" /qn /norestart" -ForegroundColor Gray
Write-Host ""
Write-Host "Uninstall (from the same MSI):" -ForegroundColor White
Write-Host "  msiexec /x `"$MsiPath`" /qb" -ForegroundColor Gray
Write-Host "  -- or via Add / Remove Programs in Windows Settings." -ForegroundColor DarkGray
Write-Host ""
