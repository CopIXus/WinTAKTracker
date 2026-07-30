#Requires -Version 5.1
<#
.SYNOPSIS
  Compile WinTAKTracker-Setup.exe with Inno Setup (after publish).

.DESCRIPTION
  Expects:
    publish\WinTAKTracker.exe
    publish\service\WinTAKTracker.Service.exe
  Installs Inno Setup via Chocolatey if ISCC.exe is missing (optional -SkipChoco).
#>
param(
    [string]$Version = "0.0.0-dev",
    [string]$VersionInfo = "",
    [switch]$SkipChoco
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$tray = Join-Path $root 'publish\WinTAKTracker.exe'
$svc = Join-Path $root 'publish\service\WinTAKTracker.Service.exe'
if (-not (Test-Path $tray)) { throw "Missing $tray — publish the tray app first." }
if (-not (Test-Path $svc)) { throw "Missing $svc — publish the service first." }

if (-not $VersionInfo) {
    if ($Version -match '^(\d+)\.(\d+)\.(\d+)') {
        $VersionInfo = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
    } else {
        $VersionInfo = "0.0.0.0"
    }
}

function Find-ISCC {
    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-ISCC
if (-not $iscc) {
    if ($SkipChoco) { throw "Inno Setup 6 (ISCC.exe) not found." }
    Write-Host "Inno Setup not found — installing via Chocolatey…"
    choco install innosetup --no-progress -y
    $iscc = Find-ISCC
    if (-not $iscc) { throw "ISCC.exe still not found after choco install innosetup." }
}

New-Item -ItemType Directory -Force -Path (Join-Path $root 'dist') | Out-Null
$iss = Join-Path $root 'installer\WinTAKTracker.iss'
Write-Host "Compiling $iss (version=$Version versionInfo=$VersionInfo)"
& $iscc /DMyAppVersion="$Version" /DMyAppVersionInfo="$VersionInfo" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit $LASTEXITCODE" }

$setup = Join-Path $root 'dist\WinTAKTracker-Setup.exe'
if (-not (Test-Path $setup)) { throw "Missing output $setup" }
Write-Host "Built $setup"
