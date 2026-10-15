<#
.SYNOPSIS
    Installs the pinned kopia.exe into tools\kopia, verified by SHA-256.

.DESCRIPTION
    Default mode: download the release zip named in kopia.pin.json, require its SHA-256 to match
    both the pin and the release's checksums.txt, extract only the pinned kopia.exe entry, check its
    SHA-256 and its --version. Does nothing if kopia.exe already matches the pin (unless -Force).

    Maintainer mode (-PinVersion X.Y.Z): look up that stable release on GitHub, verify the GPG
    signature on checksums.txt against Kopia's signing key, check the zip against checksums.txt,
    require a valid Authenticode signature from Kopia LLC on kopia.exe, fetch LICENSE at the tag,
    rewrite kopia.pin.json, then install. Review the pin diff before committing it.

    This file must stay ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1 -PinVersion 0.23.1
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$PinVersion,
    [switch]$SkipSignatureCheck
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$PinPath = Join-Path $Here 'kopia.pin.json'
$ExePath = Join-Path $Here 'kopia.exe'
$LicensePath = Join-Path $Here 'LICENSE'
$Repo = 'kopia/kopia'
$SigningKeyUrl = 'https://kopia.io/signing-key'
$SigningKeyFingerprint = '7FB99DFD47809F0D5339D7D92273699AFD56A556'
$AuthenticodeSubjectPrefix = 'CN=Kopia LLC'
$Headers = @{ 'User-Agent' = 'cashgear-fetch-kopia' }

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Download([string]$Url, [string]$OutFile) {
    Write-Host "  GET $Url"
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -Headers $Headers
}

function New-WorkDir {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('cashgear-kopia-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    return $dir
}

# Runs a native command without letting its stderr turn into a terminating error (PS 5.1).
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
    }
    finally {
        $ErrorActionPreference = $saved
    }
}

# ConvertTo-Json in PS 5.1 mis-indents nested objects; this writes ordered maps with 2-space indents.
function Format-Json($Value, [int]$Depth = 0) {
    if ($Value -is [System.Collections.IDictionary]) {
        $pad = '  ' * ($Depth + 1)
        $members = foreach ($key in $Value.Keys) {
            $pad + (ConvertTo-Json ([string]$key)) + ': ' + (Format-Json $Value[$key] ($Depth + 1))
        }
        return "{`r`n" + ($members -join ",`r`n") + "`r`n" + ('  ' * $Depth) + '}'
    }
    if ($null -eq $Value) { return 'null' }
    return (ConvertTo-Json $Value -Compress)
}

function Get-ListedChecksum([string]$ChecksumsFile, [string]$AssetName) {
    foreach ($line in Get-Content -LiteralPath $ChecksumsFile) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $AssetName) {
            return $parts[0].ToLowerInvariant()
        }
    }
    throw "checksums.txt has no entry for $AssetName."
}

function Expand-ZipEntry([string]$ZipPath, [string]$EntryName, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $zip.GetEntry($EntryName)
        if ($null -eq $entry) { throw "Zip has no entry '$EntryName'." }
        $in = $entry.Open()
        try {
            $out = [System.IO.File]::Create($Destination)
            try { $in.CopyTo($out) } finally { $out.Dispose() }
        }
        finally { $in.Dispose() }
    }
    finally { $zip.Dispose() }
}

function Assert-ZipMatches($Pin, [string]$ZipPath, [string]$ChecksumsPath) {
    $zipHash = Get-Sha256 $ZipPath
    if ($zipHash -ne $Pin.asset.sha256) {
        throw "Zip SHA-256 $zipHash does not match the pin $($Pin.asset.sha256)."
    }
    $listed = Get-ListedChecksum $ChecksumsPath $Pin.asset.name
    if ($zipHash -ne $listed) {
        throw "Zip SHA-256 $zipHash does not match the release checksums.txt ($listed)."
    }
}

function Install-FromZip($Pin, [string]$ZipPath) {
    $tmp = Join-Path $Here 'kopia.exe.tmp'
    Expand-ZipEntry $ZipPath $Pin.exe.entry $tmp
    $exeHash = Get-Sha256 $tmp
    if ($exeHash -ne $Pin.exe.sha256) {
        Remove-Item -LiteralPath $tmp -Force
        throw "kopia.exe SHA-256 $exeHash does not match the pin $($Pin.exe.sha256)."
    }
    if (Test-Path -LiteralPath $ExePath) { Remove-Item -LiteralPath $ExePath -Force }
    Move-Item -LiteralPath $tmp -Destination $ExePath

    $result = Invoke-Native $ExePath @('--version')
    $version = ($result.Output | Select-Object -First 1)
    if ($null -eq $version -or -not "$version".Trim().StartsWith($Pin.version)) {
        throw "kopia.exe --version printed '$version', expected $($Pin.version)."
    }
    Write-Host "Installed kopia.exe $($Pin.version) (sha256 $exeHash)."
}

function Install-PinnedKopia {
    if (-not (Test-Path -LiteralPath $PinPath)) { throw "Missing $PinPath." }
    $pin = Get-Content -LiteralPath $PinPath -Raw | ConvertFrom-Json

    if ((Test-Path -LiteralPath $ExePath) -and -not $Force) {
        if ((Get-Sha256 $ExePath) -eq $pin.exe.sha256) {
            Write-Host "kopia.exe $($pin.version) is already installed and matches the pin."
            return
        }
        Write-Host 'kopia.exe does not match the pin; reinstalling.'
    }

    $work = New-WorkDir
    try {
        $zipPath = Join-Path $work $pin.asset.name
        $sumsPath = Join-Path $work 'checksums.txt'
        Invoke-Download $pin.asset.url $zipPath
        Invoke-Download $pin.checksumsUrl $sumsPath
        Assert-ZipMatches $pin $zipPath $sumsPath
        Install-FromZip $pin $zipPath
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Find-Gpg {
    $cmd = Get-Command gpg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @(
            (Join-Path $env:ProgramFiles 'Git\usr\bin\gpg.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'GnuPG\bin\gpg.exe'))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

# Git for Windows ships an MSYS gpg that only understands /c/... style paths.
function ConvertTo-GpgPath([string]$Gpg, [string]$Path) {
    $isMsys = Test-Path -LiteralPath (Join-Path (Split-Path -Parent $Gpg) 'msys-2.0.dll')
    if (-not $isMsys) { return $Path }
    $full = [IO.Path]::GetFullPath($Path)
    return '/' + $full.Substring(0, 1).ToLowerInvariant() + ($full.Substring(2) -replace '\\', '/')
}

function Assert-GpgSignature([string]$DataPath, [string]$SigPath, [string]$Work) {
    $gpg = Find-Gpg
    if (-not $gpg) {
        throw 'gpg not found (install Git for Windows or Gpg4win), or pass -SkipSignatureCheck.'
    }
    $gnupgHome = Join-Path $Work 'gnupg'
    New-Item -ItemType Directory -Path $gnupgHome | Out-Null
    $keyPath = Join-Path $Work 'signing-key.asc'
    Invoke-Download $SigningKeyUrl $keyPath

    # Dearmor the key into a standalone keyring and verify with gpgv: no keyring import, no gpg-agent.
    $keyringPath = Join-Path $Work 'kopia-signing-key.gpg'
    $dearmor = Invoke-Native $gpg @('--homedir', (ConvertTo-GpgPath $gpg $gnupgHome), '--batch', '--yes',
        '--output', (ConvertTo-GpgPath $gpg $keyringPath), '--dearmor', (ConvertTo-GpgPath $gpg $keyPath))
    if ($dearmor.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $keyringPath)) {
        throw "gpg --dearmor failed:`n$($dearmor.Output -join "`n")"
    }

    $gpgv = Join-Path (Split-Path -Parent $gpg) 'gpgv.exe'
    if (-not (Test-Path -LiteralPath $gpgv)) { throw "gpgv.exe not found next to $gpg." }
    $verify = Invoke-Native $gpgv @('--status-fd', '1', '--keyring', (ConvertTo-GpgPath $gpgv $keyringPath),
        (ConvertTo-GpgPath $gpgv $SigPath), (ConvertTo-GpgPath $gpgv $DataPath))
    $validSig = $verify.Output | Where-Object { $_ -like '`[GNUPG:`] VALIDSIG *' }
    if ($verify.ExitCode -ne 0 -or -not $validSig) {
        throw "checksums.txt signature did not verify:`n$($verify.Output -join "`n")"
    }
    $fields = "$validSig" -split ' '
    $primaryFingerprint = $fields[$fields.Count - 1]
    if ($primaryFingerprint -ne $SigningKeyFingerprint) {
        throw "checksums.txt was signed by $primaryFingerprint, expected $SigningKeyFingerprint."
    }
    Write-Host "  checksums.txt GPG signature is valid (key $SigningKeyFingerprint)."
}

function Update-Pin([string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-PinVersion must look like 0.23.1, got '$Version'." }
    $tag = "v$Version"
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$tag" -Headers $Headers -UseBasicParsing
    if ($release.draft -or $release.prerelease) { throw "$tag is not a stable release." }

    $assetName = "kopia-$Version-windows-x64.zip"
    $asset = $release.assets | Where-Object { $_.name -eq $assetName }
    $sums = $release.assets | Where-Object { $_.name -eq 'checksums.txt' }
    $sig = $release.assets | Where-Object { $_.name -eq 'checksums.txt.sig' }
    if (-not $asset -or -not $sums -or -not $sig) { throw "Release $tag is missing $assetName, checksums.txt or checksums.txt.sig." }

    $work = New-WorkDir
    try {
        $zipPath = Join-Path $work $assetName
        $sumsPath = Join-Path $work 'checksums.txt'
        $sigPath = Join-Path $work 'checksums.txt.sig'
        Invoke-Download $asset.browser_download_url $zipPath
        Invoke-Download $sums.browser_download_url $sumsPath
        Invoke-Download $sig.browser_download_url $sigPath

        if ($SkipSignatureCheck) {
            Write-Warning 'Skipping the GPG signature check on checksums.txt.'
        }
        else {
            Assert-GpgSignature $sumsPath $sigPath $work
        }

        $zipHash = Get-Sha256 $zipPath
        $listed = Get-ListedChecksum $sumsPath $assetName
        if ($zipHash -ne $listed) { throw "Zip SHA-256 $zipHash does not match checksums.txt ($listed)." }

        $folder = "kopia-$Version-windows-x64"
        $exeEntry = "$folder/kopia.exe"
        $probeExe = Join-Path $work 'kopia.exe'
        Expand-ZipEntry $zipPath $exeEntry $probeExe
        $signature = Get-AuthenticodeSignature -LiteralPath $probeExe
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate.Subject.StartsWith($AuthenticodeSubjectPrefix)) {
            throw "kopia.exe Authenticode check failed: status $($signature.Status), signer '$($signature.SignerCertificate.Subject)'."
        }
        Write-Host "  kopia.exe Authenticode signature is valid ($($signature.SignerCertificate.Subject))."

        Invoke-Download "https://raw.githubusercontent.com/$Repo/$tag/LICENSE" $LicensePath
        $zipLicense = Join-Path $work 'LICENSE.zip-copy'
        Expand-ZipEntry $zipPath "$folder/LICENSE" $zipLicense
        $licenseText = (Get-Content -LiteralPath $LicensePath -Raw) -replace "`r`n", "`n"
        $zipLicenseText = (Get-Content -LiteralPath $zipLicense -Raw) -replace "`r`n", "`n"
        if ($licenseText -ne $zipLicenseText) {
            Write-Warning 'LICENSE at the tag differs from the copy inside the release zip; review it.'
        }

        $notice = $null
        try {
            Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$Repo/$tag/NOTICE" -Method Head -UseBasicParsing -Headers $Headers | Out-Null
            $notice = "https://raw.githubusercontent.com/$Repo/$tag/NOTICE"
            Write-Warning 'Kopia now ships a NOTICE file; add it to tools\kopia and the installer.'
        }
        catch {
            Write-Host '  No NOTICE file at the tag.'
        }

        $pin = [ordered]@{
            schemaVersion         = 1
            version               = $Version
            tag                   = $tag
            publishedAt           = $release.published_at
            asset                 = [ordered]@{
                name   = $assetName
                url    = $asset.browser_download_url
                size   = (Get-Item -LiteralPath $zipPath).Length
                sha256 = $zipHash
            }
            exe                   = [ordered]@{
                entry  = $exeEntry
                size   = (Get-Item -LiteralPath $probeExe).Length
                sha256 = Get-Sha256 $probeExe
            }
            checksumsUrl          = $sums.browser_download_url
            signatureUrl          = $sig.browser_download_url
            signingKeyUrl         = $SigningKeyUrl
            signingKeyFingerprint = $SigningKeyFingerprint
            authenticodeSubject   = $signature.SignerCertificate.Subject
            license               = 'Apache-2.0'
            licenseUrl            = "https://raw.githubusercontent.com/$Repo/$tag/LICENSE"
            notice                = $notice
        }
        $json = Format-Json $pin
        [IO.File]::WriteAllText($PinPath, $json + "`r`n", (New-Object Text.UTF8Encoding $false))
        Write-Host "Wrote $PinPath for Kopia $Version."

        $Script:Force = $true
        Install-FromZip ($json | ConvertFrom-Json) $zipPath
    }
    finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($PinVersion) {
    Update-Pin $PinVersion
}
else {
    Install-PinnedKopia
}
