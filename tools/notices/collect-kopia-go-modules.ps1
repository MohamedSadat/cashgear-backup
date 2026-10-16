<#
.SYNOPSIS
    Collects the Go modules compiled into the pinned kopia.exe and their licence files (maintainer tool).

.DESCRIPTION
    Reads the build info embedded in tools\kopia\kopia.exe (what `go version -m kopia.exe` prints, parsed here so
    no Go toolchain is needed), downloads each module's zip from proxy.golang.org, checks it against the module's
    h1: hash in that build info, and copies its licence and NOTICE files into tools\notices\kopia-go-licenses
    (except files holding just the Apache-2.0 text the notices already print; those are only listed, marked
    standardApache). The Go standard library's LICENSE comes from the Go release tag named in the build info.
    Writes tools\notices\kopia-go-modules.json with each module's SPDX licence ids and copyright lines.

    Licence files in a module's subfolders (code copied in from other projects) are kept only when kopia.exe
    contains source files from that subfolder; the module's root licence files are always kept.

    Run it after changing the kopia pin (NoticesTests fails until you do), review the diff, then run
    update-notices.ps1, which reads only what this script wrote. It stops on a licence file it cannot identify:
    read that file, then teach Get-SpdxIds (notices-common.ps1) the licence.

    This file must stay ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\notices\collect-kopia-go-modules.ps1
#>
[CmdletBinding()]
param(
    # Keeps the downloaded module zips here between runs (default: a new temp folder, deleted afterwards).
    [string]$CacheDir
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'notices-common.ps1')

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ExePath = Join-Path $Root 'tools\kopia\kopia.exe'
$PinPath = Join-Path $Root 'tools\kopia\kopia.pin.json'
$ApachePath = Join-Path $Root 'tools\kopia\LICENSE'
$DataPath = Join-Path $PSScriptRoot 'kopia-go-modules.json'
$LicenceDir = Join-Path $PSScriptRoot 'kopia-go-licenses'
$Headers = @{ 'User-Agent' = 'cashgear-collect-kopia-go-modules' }
$Utf8 = New-Object Text.UTF8Encoding($false)

# The build info Go 1.18+ writes into every binary: a 32-byte header starting with "\xff Go buildinf:", then (flag 2,
# inline strings) the Go version and the module info, each prefixed with its length as a uvarint. The module info is
# wrapped in two 16-byte binary sentinels. Same data as `go version -m`.
function Read-GoBuildInfo([byte[]]$Bytes, [string]$Text) {
    $at = $Text.IndexOf(([string][char]0xff) + ' Go buildinf:', [StringComparison]::Ordinal)
    if ($at -lt 0) { throw 'kopia.exe has no Go build info.' }
    if (($Bytes[$at + 15] -band 2) -eq 0) { throw 'kopia.exe uses the pre-Go 1.18 build info layout, which this script does not read.' }
    $script:pos = $at + 32
    $readLength = {
        $length = 0; $shift = 0
        while ($true) {
            $b = $Bytes[$script:pos]; $script:pos++
            $length = $length -bor (([int]($b -band 0x7f)) -shl $shift)
            if ($b -lt 0x80) { return $length }
            $shift += 7
        }
    }
    $length = & $readLength
    $goVersion = [Text.Encoding]::UTF8.GetString($Bytes, $script:pos, $length)
    $script:pos += $length
    $length = & $readLength
    if ($length -le 32) { throw 'kopia.exe has no module info in its build info.' }
    $modInfo = [Text.Encoding]::UTF8.GetString($Bytes, $script:pos + 16, $length - 32)

    $main = $null
    $deps = New-Object System.Collections.Generic.List[object]
    foreach ($line in $modInfo.Split("`n")) {
        $f = $line.Split("`t")
        switch ($f[0]) {
            'mod' { $main = [pscustomobject]@{ Path = $f[1]; Version = $f[2] } }
            # A replaced module's dep line has no sum; its "=>" line does.
            'dep' { $deps.Add([pscustomobject]@{ Path = $f[1]; Version = $f[2]; Sum = $(if ($f.Count -gt 3) { $f[3] } else { '' }); Replace = $null }) }
            '=>' { $deps[$deps.Count - 1].Replace = [pscustomobject]@{ Path = $f[1]; Version = $f[2]; Sum = $f[3] } }
        }
    }
    return [pscustomobject]@{ GoVersion = $goVersion.Split(' ')[0]; Main = $main; Deps = $deps }
}

# Module paths and versions on the proxy: each upper-case letter becomes '!' and its lower-case form.
function ConvertTo-ProxyEscaped([string]$Value) {
    $sb = New-Object Text.StringBuilder
    foreach ($c in $Value.ToCharArray()) {
        if ([char]::IsUpper($c)) { [void]$sb.Append('!').Append([char]::ToLowerInvariant($c)) } else { [void]$sb.Append($c) }
    }
    return $sb.ToString()
}

function Get-ModuleLicence($Files) {
    return ((@($Files | Where-Object { -not $_.Notice } | ForEach-Object { $_.Ids }) | Select-Object -Unique) -join ' AND ')
}

$pin = Get-Content $PinPath -Raw | ConvertFrom-Json
if (-not (Test-Path $ExePath)) { throw "$ExePath is missing: run tools\kopia\fetch-kopia.ps1 first." }
$exeBytes = [IO.File]::ReadAllBytes($ExePath)
if ((Get-Sha256Hex $exeBytes) -ne $pin.exe.sha256) { throw "$ExePath does not match the pin: run tools\kopia\fetch-kopia.ps1 -Force first." }
# Latin-1 maps every byte to one char, so string searches find byte sequences.
$exeText = [Text.Encoding]::GetEncoding(28591).GetString($exeBytes)

$info = Read-GoBuildInfo $exeBytes $exeText
if ($info.Main.Version -ne $pin.tag) { throw "kopia.exe was built from $($info.Main.Path) $($info.Main.Version), the pin says $($pin.tag)." }
Write-Host "kopia $($pin.version): $($info.Deps.Count) modules, $($info.GoVersion)."

Initialize-StandardApache $ApachePath

$ownCache = -not $CacheDir
if ($ownCache) { $CacheDir = Join-Path ([IO.Path]::GetTempPath()) ('cashgear-go-modules-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
if (Test-Path $LicenceDir) { Remove-Item -Recurse -Force $LicenceDir }
New-Item -ItemType Directory -Path $LicenceDir | Out-Null

$problems = New-Object System.Collections.Generic.List[string]
$modules = New-Object System.Collections.Generic.List[object]
try {
    # The standard library and runtime, under the LICENSE at the Go release tag.
    $goUrl = "https://raw.githubusercontent.com/golang/go/$($info.GoVersion)/LICENSE"
    Write-Host "  GET $goUrl"
    $goLicence = (Invoke-WebRequest -Uri $goUrl -UseBasicParsing -Headers $Headers).Content
    New-Item -ItemType Directory -Path (Join-Path $LicenceDir 'std') | Out-Null
    [IO.File]::WriteAllText((Join-Path $LicenceDir 'std\LICENSE'), $goLicence, $Utf8)
    $goFile = New-FileRecord 'LICENSE' $goLicence
    if (-not $goFile.License) { $problems.Add("Go $($info.GoVersion): LICENSE") }

    foreach ($dep in $info.Deps) {
        $source = if ($dep.Replace) { $dep.Replace } else { $dep }
        if ($source.Path.StartsWith('.') -or -not $source.Sum) { throw "$($dep.Path) is replaced by a local folder; collect its licence by hand." }
        $zipName = (Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes("$($source.Path)@$($source.Version)"))).Substring(0, 16) + '.zip'
        $zipPath = Join-Path $CacheDir $zipName
        if (-not (Test-Path $zipPath)) {
            $url = 'https://proxy.golang.org/{0}/@v/{1}.zip' -f (ConvertTo-ProxyEscaped $source.Path), (ConvertTo-ProxyEscaped $source.Version)
            Write-Host "  GET $url"
            Invoke-WebRequest -Uri $url -OutFile "$zipPath.part" -UseBasicParsing -Headers $Headers
            Move-Item "$zipPath.part" $zipPath
        }

        $files = New-Object System.Collections.Generic.List[object]
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $hash = Get-ModuleZipHash $zip
            if ($hash -ne $source.Sum) { throw "$($source.Path) $($source.Version): zip hash $hash, build info says $($source.Sum)." }
            $prefix = '{0}@{1}/' -f $source.Path, $source.Version
            foreach ($entry in ($zip.Entries | Sort-Object FullName)) {
                if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "$($entry.FullName) is outside $prefix." }
                $relative = $entry.FullName.Substring($prefix.Length)
                if ($entry.Name -notmatch $LicenceFilePattern -or $relative -match '(^|/)(testdata|vendor|\.[^/]*|_[^/]*)/') { continue }
                if ($relative.Split('/') -contains '..') { throw "$($entry.FullName) has a '..' segment." }
                $dir = $relative.Substring(0, [Math]::Max(0, $relative.LastIndexOf('/')))
                # -trimpath builds name source files "<module>@<version>/<dir>/<file>.go".
                if ($dir -and -not $exeText.Contains("$prefix$dir/")) {
                    Write-Host "  skipped $($dep.Path)/$relative (no code from $dir in kopia.exe)"
                    continue
                }
                $bytes = Read-EntryBytes $entry
                $file = New-FileRecord $relative ([Text.Encoding]::UTF8.GetString($bytes))
                if (-not $file.Notice -and -not $file.License) { $problems.Add("$($dep.Path) $($dep.Version): $relative") }
                $files.Add($file)
                if (-not $file.StandardApache) {
                    $target = Join-Path $LicenceDir (($dep.Path + '/' + $relative) -replace '/', '\')
                    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                    [IO.File]::WriteAllBytes($target, $bytes)
                }
            }
        } finally { $zip.Dispose() }

        if (-not ($files | Where-Object { -not $_.Notice -and -not $_.Path.Contains('/') })) {
            $problems.Add("$($dep.Path) $($dep.Version): no licence file at the module root")
        }
        $modules.Add([pscustomobject]@{ Dep = $dep; Files = $files })
    }
} finally {
    if ($ownCache) { Remove-Item -Recurse -Force $CacheDir -ErrorAction SilentlyContinue }
}
if ($problems.Count -gt 0) { throw "Licence not identified, read these files and extend Get-SpdxIds:`n  " + ($problems -join "`n  ") }

$json = New-Object Text.StringBuilder
[void]$json.Append("{`n")
[void]$json.Append("  `"schemaVersion`": 1,`n")
[void]$json.Append("  `"kopiaVersion`": $(ConvertTo-JsonString $pin.version),`n")
[void]$json.Append("  `"kopiaExeSha256`": $(ConvertTo-JsonString $pin.exe.sha256),`n")
[void]$json.Append("  `"go`": {`n")
[void]$json.Append("    `"version`": $(ConvertTo-JsonString $info.GoVersion),`n")
[void]$json.Append("    `"license`": $(ConvertTo-JsonString $goFile.License),`n")
[void]$json.Append("    `"copyright`": $(Format-StringArray $goFile.Copyright),`n")
[void]$json.Append("    `"files`": $(Format-Files @($goFile) '    ')`n")
[void]$json.Append("  },`n")
[void]$json.Append("  `"modules`": [`n")
$entries = foreach ($m in $modules) {
    $copyright = @($m.Files | ForEach-Object { $_.Copyright }) | Select-Object -Unique
    $lines = @(
        "    {",
        "      `"path`": $(ConvertTo-JsonString $m.Dep.Path),",
        "      `"version`": $(ConvertTo-JsonString $m.Dep.Version),",
        "      `"sum`": $(ConvertTo-JsonString $m.Dep.Sum),",
        "      `"license`": $(ConvertTo-JsonString (Get-ModuleLicence $m.Files)),",
        "      `"copyright`": $(Format-StringArray $copyright),",
        "      `"files`": $(Format-Files $m.Files '      ')",
        "    }")
    $lines -join "`n"
}
[void]$json.Append(($entries -join ",`n")).Append("`n  ]`n}`n")
[IO.File]::WriteAllText($DataPath, $json.ToString(), $Utf8)
Write-Host "Wrote $DataPath ($($modules.Count) modules) and $LicenceDir."
