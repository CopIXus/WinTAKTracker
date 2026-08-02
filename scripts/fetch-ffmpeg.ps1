#Requires -Version 5.1
<#
.SYNOPSIS
  Download a pinned Windows FFmpeg essentials build into the publish folder for Setup bundling.

.DESCRIPTION
  FFmpeg is a separate GPLv3 program (gyan.dev / GyanD essentials). We ship ffmpeg.exe
  beside WinTAKTracker and invoke it as an external process (not linked). See
  docs/third-party-ffmpeg.md.
#>
param(
    [string]$OutDir = "publish",
    [string]$Version = "8.1",
    [string]$Url = ""
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

if (-not $Url) {
    $Url = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/ffmpeg-$Version-essentials_build.zip"
}

$out = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null
$destExe = Join-Path $out 'ffmpeg.exe'
$notice = Join-Path $out 'THIRD_PARTY_FFMPEG.txt'

if ((Test-Path $destExe) -and (Test-Path $notice)) {
    Write-Host "FFmpeg already present: $destExe"
    exit 0
}

$tmpRoot = Join-Path $env:TEMP ("wtt-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
try {
    $zip = Join-Path $tmpRoot 'ffmpeg.zip'
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $tmpRoot 'extract') -Force
    $bin = Get-ChildItem -Path (Join-Path $tmpRoot 'extract') -Recurse -Filter 'ffmpeg.exe' |
        Select-Object -First 1
    if (-not $bin) { throw "ffmpeg.exe not found in archive." }
    Copy-Item $bin.FullName $destExe -Force
    $hash = (Get-FileHash -Algorithm SHA256 $destExe).Hash.ToLowerInvariant()
    @"
FFmpeg (Windows essentials build)
=================================
Source: $Url
Version tag: $Version
SHA256 (ffmpeg.exe): $hash

FFmpeg is copyright the FFmpeg developers and licensed under the GNU GPL v3
for this essentials build (see https://www.gyan.dev/ffmpeg/builds/).
WinTAKTracker invokes ffmpeg.exe as a separate process and does not link
against FFmpeg libraries. Upstream project: https://ffmpeg.org/
License text: https://www.gnu.org/licenses/gpl-3.0.html
"@ | Set-Content -Path $notice -Encoding UTF8
    Write-Host "Staged $destExe"
    & $destExe -hide_banner -version | Select-Object -First 2
}
finally {
    Remove-Item -Recurse -Force $tmpRoot -ErrorAction SilentlyContinue
}
