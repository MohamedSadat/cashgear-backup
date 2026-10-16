<#
.SYNOPSIS
    Installs the pinned test servers (SFTPGo for SFTP, versitygw for S3) into tools\test-servers.

.DESCRIPTION
    For each server in test-servers.pin.json: download the release zip, require its SHA-256 to match the
    pin, extract it into tools\test-servers\<name>\, and require the executable's SHA-256 to match too.
    Does nothing for a server whose executable already matches (unless -Force). Test-only: these never
    ship, and the tests start them on 127.0.0.1.

    This file must stay ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-servers\fetch-test-servers.ps1
#>
[CmdletBinding()]
param([switch]$Force)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Pin = Get-Content -Raw -Path (Join-Path $Here 'test-servers.pin.json') | ConvertFrom-Json

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant() }

foreach ($server in $Pin.servers) {
    $folder = Join-Path $Here $server.name
    $exe = Join-Path $folder $server.exe
    if (-not $Force -and (Test-Path $exe) -and (Get-Sha256 $exe) -eq $server.exeSha256) {
        Write-Host "$($server.name) $($server.version) is installed and matches the pin."
        continue
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("cashgear-test-server-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $zip = Join-Path $temp 'download.zip'
        Write-Host "Downloading $($server.name) $($server.version)..."
        Invoke-WebRequest -UseBasicParsing -Uri $server.url -OutFile $zip
        $zipHash = Get-Sha256 $zip
        if ($zipHash -ne $server.zipSha256) {
            throw "$($server.name): the download's SHA-256 $zipHash does not match the pin $($server.zipSha256)."
        }

        if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
        Expand-Archive -Path $zip -DestinationPath $folder
        $exeHash = Get-Sha256 $exe
        if ($exeHash -ne $server.exeSha256) {
            Remove-Item -Recurse -Force $folder
            throw "$($server.name): $($server.exe) has SHA-256 $exeHash, not the pinned $($server.exeSha256)."
        }

        Write-Host "$($server.name) $($server.version) installed in $folder."
    }
    finally {
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }
}
