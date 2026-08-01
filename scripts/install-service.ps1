#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install or update the WinTAKTracker Windows Service (always-on PLI).

.DESCRIPTION
  Copies WinTAKTracker.Service.exe (and deps) to Program Files, creates the SCM service
  (auto-start), and starts it. Optionally migrates %LocalAppData%\WinTAKTracker config into
  %ProgramData%\WinTAKTracker\ (LocalMachine DPAPI for secrets).

  Machine store ACL:
  - Root / logs / updates: SYSTEM + Administrators Full; Authenticated Users Modify
  - secrets / certs: SYSTEM + Administrators Full only (tray mutates secrets via IPC when possible)

.PARAMETER SourceDir
  Folder containing the published service binaries (default: adjacent publish\service).

.PARAMETER InstallDir
  Install location (default: %ProgramFiles%\WinTAKTracker).

.PARAMETER MigrateUserConfig
  Copy/migrate the current user's LocalAppData config into ProgramData before start.

.PARAMETER RegisterOnly
  Skip copying binaries (files already in InstallDir). Only (re)register and start the SCM service.
  Used by the Inno Setup installer after it stages files under Program Files.
#>
param(
    [string]$SourceDir = "",
    [string]$InstallDir = "$env:ProgramFiles\WinTAKTracker",
    [switch]$MigrateUserConfig,
    [switch]$RegisterOnly,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'WinTAKTracker'
$DisplayName = 'WinTAKTracker'
$PipeHint = 'Named pipe: WinTAKTracker.Control'

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = [Security.Principal.WindowsPrincipal]::new($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Set-MachineStoreAcl {
    param([string]$MachineRoot)

    New-Item -ItemType Directory -Force -Path $MachineRoot | Out-Null
    $secrets = Join-Path $MachineRoot 'secrets'
    $certs = Join-Path $MachineRoot 'certs'
    $logs = Join-Path $MachineRoot 'logs'
    $updates = Join-Path $MachineRoot 'updates'
    foreach ($d in @($secrets, $certs, $logs, $updates)) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
    }

    # S-1-5-18 SYSTEM; S-1-5-32-544 Administrators; S-1-5-11 Authenticated Users
    icacls $MachineRoot /inheritance:r | Out-Null
    icacls $MachineRoot /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-11:(OI)(CI)M' | Out-Null
    icacls $logs /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-11:(OI)(CI)M' | Out-Null
    icacls $updates /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-11:(OI)(CI)M' | Out-Null
    icacls $secrets /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    icacls $certs /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    Write-Host "Machine store ACL: Authenticated Users Modify on root/logs/updates; secrets/certs SYSTEM+Admins only → $MachineRoot"
}

if (-not (Test-IsAdmin)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

if ($Uninstall) {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -eq 'Running') { Stop-Service -Name $ServiceName -Force }
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
        Write-Host "Service '$ServiceName' deleted."
    }
    if (Test-Path $InstallDir) {
        Write-Host "Leaving files in $InstallDir — remove manually if desired."
        Write-Host "Config/secrets may remain under $env:ProgramData\WinTAKTracker\"
    }
    return
}

if ($RegisterOnly) {
    $SourceDir = $InstallDir
} elseif (-not $SourceDir) {
    $repoPublish = Join-Path $PSScriptRoot '..\publish\service'
    if (Test-Path (Join-Path $repoPublish 'WinTAKTracker.Service.exe')) {
        $SourceDir = (Resolve-Path $repoPublish).Path
    } else {
        throw "SourceDir not set and publish\service not found. Publish first:`n  dotnet publish src/WinTAKTracker.Service -c Release -r win-x64 -o publish/service"
    }
}

$exe = Join-Path $SourceDir 'WinTAKTracker.Service.exe'
if (-not (Test-Path $exe)) {
    throw "WinTAKTracker.Service.exe not found in $SourceDir"
}

$binPath = Join-Path $InstallDir 'WinTAKTracker.Service.exe'
if (-not $RegisterOnly) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Write-Host "Copying binaries → $InstallDir"
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $InstallDir -Recurse -Force
} elseif (-not (Test-Path $binPath)) {
    throw "RegisterOnly set but WinTAKTracker.Service.exe not found in $InstallDir"
}
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq 'Running') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $ServiceName (auto-start, LocalSystem)"
sc.exe create $ServiceName binPath= "`"$binPath`"" start= auto DisplayName= "$DisplayName" | Out-Null
sc.exe description $ServiceName "Always-on TAK PLI tracker (NMEA/Mesh/TAK). Tray UI is a companion controller. $PipeHint" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

$machineRoot = Join-Path $env:ProgramData 'WinTAKTracker'
Set-MachineStoreAcl -MachineRoot $machineRoot

if ($MigrateUserConfig) {
    $userRoot = Join-Path $env:LOCALAPPDATA 'WinTAKTracker'
    if (Test-Path (Join-Path $userRoot 'config.json')) {
        Copy-Item (Join-Path $userRoot 'config.json') (Join-Path $machineRoot 'config.json') -Force
        foreach ($sub in @('certs')) {
            $from = Join-Path $userRoot $sub
            $to = Join-Path $machineRoot $sub
            if (Test-Path $from) {
                New-Item -ItemType Directory -Force -Path $to | Out-Null
                Copy-Item (Join-Path $from '*') $to -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        # Re-assert certs ACL after copy
        icacls (Join-Path $machineRoot 'certs') /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        Write-Host "Copied config/certs to $machineRoot"
        Write-Host "NOTE: DPAPI CurrentUser secret blobs cannot be read as LocalSystem."
        Write-Host "      Re-enter tokens/passwords in Settings after install, or run migration while elevated from the enrolled user (service re-protects on first successful CU read during migrate API)."
        Write-Host "      Prefer mutating secrets via the tray → service IPC when the service is running."
    }
}

Start-Service -Name $ServiceName
Write-Host "Service started. Status:" (Get-Service $ServiceName).Status
Write-Host "Machine config: $env:ProgramData\WinTAKTracker\"
Write-Host "Launch WinTAKTracker.exe (tray) to control the service — it will attach via IPC and not start a second tracker."
