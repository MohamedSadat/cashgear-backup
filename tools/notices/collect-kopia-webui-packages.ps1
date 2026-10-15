<#
.SYNOPSIS
    Collects the npm packages compiled into Kopia's web UI inside the pinned kopia.exe and their licence files
    (maintainer tool).

.DESCRIPTION
    kopia.exe embeds Kopia's web UI as the Go module github.com/kopia/htmluibuild: a Vite production build of
    https://github.com/kopia/htmlui whose licence comments are stripped. This script works out which npm packages are
    in that build, without npm or a build:

    1. Takes the htmluibuild version and h1: hash from kopia-go-modules.json (collect-kopia-go-modules.ps1 writes it),
       downloads that module from proxy.golang.org, checks the hash, and reads the htmlui commit the build was made
       from out of its build/index.html ("Version kopia/htmlui <commit> built on ...").
    2. Downloads kopia/htmlui at that commit and follows the imports from index.html's module script through the app's
       sources into node_modules, resolving them the way Vite's production build for browsers does: package.json
       exports (browser, module, production, import or require, default), the browser field, browser/module before
       main, and Vite's extensions. JSX files also import react/jsx-runtime (Vite's React plugin adds that).
       Versions and tarballs come from package-lock.json; each tarball must match its integrity hash. A package
       counts when the walk reaches one of its files. Tree-shaking is not modelled, so a package whose code the build
       then dropped can still be listed; packages that only Node code loads (axios's HTTP adapter) and type-only
       packages are not reached. The run prints the dependencies it did not reach.
    3. Adds Vite itself, whose module-preload polyfill is in the bundle.
    4. Copies the licence and NOTICE files at the top of each package into
       tools\notices\kopia-webui-licenses\<name>@<version>, except files holding just the Apache-2.0 text the notices
       already print (those are only listed, marked standardApache). When package.json names a licence that none of
       those files contains and the package only links to (Font Awesome's icons are CC-BY-4.0), its text comes from
       the SPDX licence list (spdx/license-list-data, tag v<SpdxVersion>) into kopia-webui-licenses\spdx.
    Writes tools\notices\kopia-webui-packages.json.

    Run it after collect-kopia-go-modules.ps1 whenever the htmluibuild version in kopia-go-modules.json changes
    (NoticesTests fails until you do), review the diff, then run update-notices.ps1, which reads only what this
    script wrote. It stops on an import it cannot resolve and on a licence it cannot identify.

    This file must stay ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\notices\collect-kopia-webui-packages.ps1
#>
[CmdletBinding()]
param(
    # Keeps the downloads here between runs (default: a new temp folder, deleted afterwards).
    [string]$CacheDir,
    # The SPDX licence list release that texts a package only links to come from.
    [string]$SpdxVersion = '3.29.0'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, System.Web.Extensions
. (Join-Path $PSScriptRoot 'notices-common.ps1')

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ApachePath = Join-Path $Root 'tools\kopia\LICENSE'
$GoDataPath = Join-Path $PSScriptRoot 'kopia-go-modules.json'
$DataPath = Join-Path $PSScriptRoot 'kopia-webui-packages.json'
$LicenceDir = Join-Path $PSScriptRoot 'kopia-webui-licenses'
$Headers = @{ 'User-Agent' = 'cashgear-collect-kopia-webui-packages' }
$Utf8 = New-Object Text.UTF8Encoding($false)

$HtmlUiBuildModule = 'github.com/kopia/htmluibuild'
$HtmlUiRepository = 'https://github.com/kopia/htmlui'
# Vite's LICENSE.md goes on to list the licences of the code bundled into Vite itself, which runs at build time and
# is not in the UI, so only the part before that list is kept.
$BuildTool = 'vite'
$BuildToolNote = "build tool; the bundle contains its module-preload polyfill"
$BuildToolLicenceEnd = '# Licenses of bundled dependencies'
$Extensions = @('.mjs', '.js', '.mts', '.ts', '.jsx', '.tsx', '.json')
$ScriptExtensions = @('.mjs', '.js', '.cjs', '.mts', '.ts', '.jsx', '.tsx')
$NodeBuiltins = @('assert', 'buffer', 'child_process', 'crypto', 'events', 'fs', 'http', 'http2', 'https', 'net', 'os',
    'path', 'process', 'querystring', 'stream', 'string_decoder', 'tls', 'tty', 'url', 'util', 'vm', 'worker_threads', 'zlib')
# Licences a package may name in package.json while its files only link to them.
$LinkedLicences = @('CC-BY-4.0')

# import ... from "x", export ... from "x", import "x", import("x"), require("x").
$ImportPattern = [regex]'(?:\bfrom|\bimport\s*\(?|\brequire\s*\()\s*(["''])([^"''\s]+)\1'
$CssImportPattern = [regex]'@import\s+(?:url\(\s*)?(["''])([^"'']+)\1'
$CssUrlPattern = [regex]'url\(\s*(["'']?)([^"''()\s]+)\1\s*\)'

$Json = New-Object Web.Script.Serialization.JavaScriptSerializer
$Json.MaxJsonLength = [int]::MaxValue
$EmptyModule = [pscustomobject]@{ Key = $null; Path = $null }

function Read-Json([byte[]]$Bytes) { return , $Json.DeserializeObject([Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF)) }

function Test-JsonObject($Value) { return $Value -is [Collections.Generic.Dictionary[string, object]] }

function Get-Field($Object, [string]$Name) {
    if ((Test-JsonObject $Object) -and $Object.ContainsKey($Name)) { return , $Object[$Name] }
    return $null
}

function Get-Download([string]$Url) {
    $path = Join-Path $CacheDir ((Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($Url))).Substring(0, 16) + '.bin')
    if (-not (Test-Path $path)) {
        Write-Host "  GET $Url"
        Invoke-WebRequest -Uri $Url -OutFile "$path.part" -UseBasicParsing -Headers $Headers
        Move-Item "$path.part" $path
    }
    return , [IO.File]::ReadAllBytes($path)
}

function Sort-Ordinal($Values) {
    $array = [string[]]@($Values)
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return , $array
}

# Joins a relative path onto a folder inside a package (or the app), resolving "." and "..".
function Join-UrlPath([string]$Base, [string]$Relative) {
    $parts = New-Object Collections.Generic.List[string]
    foreach ($part in "$Base/$Relative".Split('/')) {
        if ($part -eq '' -or $part -eq '.') { continue }
        if ($part -eq '..') {
            if ($parts.Count -eq 0) { throw "'$Relative' from '$Base' leaves its package." }
            $parts.RemoveAt($parts.Count - 1)
        } else { $parts.Add($part) }
    }
    return $parts -join '/'
}

function Get-Folder([string]$Path) { $i = $Path.LastIndexOf('/'); if ($i -lt 0) { return '' } return $Path.Substring(0, $i) }

function Read-ZipFiles([byte[]]$Bytes) {
    $zip = New-Object IO.Compression.ZipArchive((New-Object IO.MemoryStream(, $Bytes)))
    return , $zip
}

function Read-TarString([byte[]]$Tar, [int]$Offset, [int]$Length) {
    $end = [Array]::IndexOf($Tar, [byte]0, $Offset, $Length)
    if ($end -lt 0) { $end = $Offset + $Length }
    return [Text.Encoding]::UTF8.GetString($Tar, $Offset, $end - $Offset)
}

# An npm tarball: a gzipped ustar archive with every path under one top folder (usually "package/"). Returns its
# files by their path below that folder.
function Read-Tarball([byte[]]$Bytes) {
    $gzip = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(, $Bytes)), [IO.Compression.CompressionMode]::Decompress)
    $ms = New-Object IO.MemoryStream
    try { $gzip.CopyTo($ms) } finally { $gzip.Dispose() }
    $tar = $ms.ToArray()
    $files = New-Object 'Collections.Generic.Dictionary[string, byte[]]' ([StringComparer]::Ordinal)
    $pos = 0
    $longName = $null
    while ($pos + 512 -le $tar.Length) {
        $name = Read-TarString $tar $pos 100
        if (-not $name) { break }
        $sizeText = (Read-TarString $tar ($pos + 124) 12).Trim()
        $size = if ($sizeText) { [Convert]::ToInt64($sizeText, 8) } else { 0 }
        $type = [char]$tar[$pos + 156]
        if ((Read-TarString $tar ($pos + 257) 6).StartsWith('ustar')) {
            $prefix = Read-TarString $tar ($pos + 345) 155
            if ($prefix) { $name = "$prefix/$name" }
        }
        $data = $pos + 512
        if ($data + $size -gt $tar.Length) { throw 'The tarball is truncated.' }
        switch -CaseSensitive ($type) {
            # pax header for the next entry: "<length> <key>=<value>" lines.
            'x' { foreach ($record in [Text.Encoding]::UTF8.GetString($tar, $data, $size).Split("`n")) { if ($record -match '^\d+ path=(.*)$') { $longName = $Matches[1] } } }
            'L' { $longName = Read-TarString $tar $data $size }
            { $_ -eq '0' -or $_ -eq [char]0 } {
                $path = if ($longName) { $longName } else { $name }
                $longName = $null
                $slash = $path.IndexOf('/')
                if ($slash -gt 0) {
                    $relative = $path.Substring($slash + 1)
                    if ($relative.Split('/') -contains '..') { throw "$path has a '..' segment." }
                    $bytes = New-Object byte[] $size
                    [Array]::Copy($tar, $data, $bytes, 0, $size)
                    $files[$relative] = $bytes
                }
            }
            default { $longName = $null }
        }
        $pos = $data + [long]([Math]::Ceiling($size / 512.0) * 512)
    }
    return , $files
}

# The lockfile key of the package a file in package $FromKey ('' = the app) gets for "$Name": Node's lookup through
# the nested node_modules folders, as npm laid them out.
function Resolve-PackageKey([string]$FromKey, [string]$Name) {
    $dir = $FromKey
    while ($true) {
        $candidate = if ($dir) { "$dir/node_modules/$Name" } else { "node_modules/$Name" }
        if ($Lock.ContainsKey($candidate)) { return $candidate }
        if (-not $dir) { return $null }
        $i = $dir.LastIndexOf('/node_modules/')
        $dir = if ($i -ge 0) { $dir.Substring(0, $i) } else { '' }
    }
}

# A package's files and what resolving needs from its package.json, downloaded on first use.
function Get-Container([string]$Key) {
    if ($Containers.ContainsKey($Key)) { return $Containers[$Key] }
    $entry = $Lock[$Key]
    $resolved = Get-Field $entry 'resolved'
    $integrity = Get-Field $entry 'integrity'
    if ($resolved -notmatch '^https://registry\.npmjs\.org/' -or $integrity -notmatch '^sha512-') {
        throw "$Key in package-lock.json is not a registry.npmjs.org tarball with a sha512 integrity."
    }
    $bytes = Get-Download $resolved
    $sha = [Security.Cryptography.SHA512]::Create()
    try { $actual = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash($bytes)) } finally { $sha.Dispose() }
    if ($actual -ne $integrity) { throw "$resolved is $actual, package-lock.json says $integrity." }
    $files = Read-Tarball $bytes
    if (-not $files.ContainsKey('package.json')) { throw "$resolved has no package.json." }
    $json = Read-Json $files['package.json']
    $name = $Key.Substring($Key.LastIndexOf('node_modules/') + 'node_modules/'.Length)
    $version = Get-Field $entry 'version'
    if ((Get-Field $json 'name') -ne $name -or (Get-Field $json 'version') -ne $version) {
        throw "$resolved holds $(Get-Field $json 'name') $(Get-Field $json 'version'), package-lock.json says $name $version."
    }
    $fileMap = @{}
    $moduleMap = @{}
    $browser = Get-Field $json 'browser'
    if (Test-JsonObject $browser) {
        foreach ($from in $browser.Keys) {
            $to = $browser[$from]
            if ($from.StartsWith('.')) {
                $fileMap[(Join-UrlPath '' $from)] = $(if ($to -is [string]) { Join-UrlPath '' $to } else { $false })
            } else {
                $moduleMap[$from] = $(if ($to -is [string]) { $to } else { $false })
            }
        }
    }
    $container = [pscustomobject]@{
        Key = $Key; Name = $name; Version = $version; Integrity = $integrity; Json = $json; Files = $files
        FileMap = $fileMap; ModuleMap = $moduleMap
    }
    $Containers[$Key] = $container
    return $container
}

function Find-File($Files, [string]$Path) {
    if ($Path -and $Files.ContainsKey($Path)) { return $Path }
    foreach ($extension in $Extensions) { if ($Files.ContainsKey("$Path$extension")) { return "$Path$extension" } }
    return $null
}

# A path inside a package: the file itself, with one of Vite's extensions, or a folder (its own package.json, else
# index). Then the package's browser field may swap the file for another or for nothing.
function Resolve-File([string]$Key, [string]$Path) {
    $container = Get-Container $Key
    $found = Find-File $container.Files $Path
    if (-not $found -and $Path) {
        if ($container.Files.ContainsKey("$Path/package.json")) { return Resolve-Main $Key $Path }
        $found = Find-File $container.Files "$Path/index"
    }
    if (-not $found) { return $null }
    foreach ($candidate in @($found, ($found -replace '\.[^./]+$', ''))) {
        if ($container.FileMap.ContainsKey($candidate)) {
            $mapped = $container.FileMap[$candidate]
            if ($mapped -is [bool]) { return $EmptyModule }
            $found = Find-File $container.Files $mapped
            if (-not $found) { return $null }
            break
        }
    }
    return [pscustomobject]@{ Key = $Key; Path = $found }
}

# The entry of a package without "exports", or of a folder in it with its own package.json.
function Resolve-Main([string]$Key, [string]$Dir) {
    $container = Get-Container $Key
    $json = if ($Dir) { Read-Json $container.Files["$Dir/package.json"] } else { $container.Json }
    foreach ($field in 'browser', 'module', 'jsnext:main', 'jsnext', 'main') {
        $value = Get-Field $json $field
        if ($value -is [string] -and $value) {
            $path = Join-UrlPath $Dir $value
            if ($path -ne $Dir) {
                $found = Resolve-File $Key $path
                if ($found) { return $found }
            }
        }
    }
    return Resolve-File $Key (Join-UrlPath $Dir 'index')
}

function Resolve-ExportTarget($Target, [string[]]$Conditions, [string]$Star) {
    if ($Target -is [string]) { return $Target.Replace('*', $Star) }
    if ($Target -is [object[]]) {
        foreach ($item in $Target) { $found = Resolve-ExportTarget $item $Conditions $Star; if ($found) { return $found } }
    } elseif (Test-JsonObject $Target) {
        foreach ($condition in $Target.Keys) {
            if ($Conditions -ccontains $condition) {
                $found = Resolve-ExportTarget $Target[$condition] $Conditions $Star
                if ($found) { return $found }
            }
        }
    }
    return $null
}

# package.json "exports" for a subpath ("." or "./x"), with the conditions of Vite's browser production build.
function Resolve-Exports($Exports, [string]$Subpath, [string]$Kind) {
    $conditions = @('browser', 'module', 'production', $Kind, 'default')
    $map = $Exports
    if (-not (Test-JsonObject $Exports) -or -not (@($Exports.Keys) | Where-Object { $_.StartsWith('.') })) { $map = @{ '.' = $Exports } }
    if ($map.ContainsKey($Subpath)) { return Resolve-ExportTarget $map[$Subpath] $conditions '' }
    $best = $null
    foreach ($pattern in @($map.Keys)) {
        $star = $pattern.IndexOf('*')
        if ($star -lt 0) { continue }
        $prefix = $pattern.Substring(0, $star)
        $suffix = $pattern.Substring($star + 1)
        if ($Subpath.Length -ge $prefix.Length + $suffix.Length -and $Subpath.StartsWith($prefix, [StringComparison]::Ordinal) -and
            $Subpath.EndsWith($suffix, [StringComparison]::Ordinal) -and (-not $best -or $prefix.Length -gt $best.Prefix.Length)) {
            $best = [pscustomobject]@{ Pattern = $pattern; Prefix = $prefix; Star = $Subpath.Substring($prefix.Length, $Subpath.Length - $prefix.Length - $suffix.Length) }
        }
    }
    if ($best) { return Resolve-ExportTarget $map[$best.Pattern] $conditions $best.Star }
    return $null
}

# What an import in $From resolves to: a file, $EmptyModule (the browser field drops it, or a Node built-in), or
# $null when it cannot be resolved.
function Resolve-Import($From, [string]$Spec, [string]$Kind) {
    $container = Get-Container $From.Key
    if ($Spec.StartsWith('.') -or $Spec.StartsWith('/')) {
        $base = if ($Spec.StartsWith('/')) { '' } else { Get-Folder $From.Path }
        return Resolve-File $From.Key (Join-UrlPath $base $Spec)
    }
    if ($container.ModuleMap.ContainsKey($Spec)) {
        $mapped = $container.ModuleMap[$Spec]
        if ($mapped -is [bool]) { return $EmptyModule }
        if ($mapped.StartsWith('.')) { return Resolve-File $From.Key (Join-UrlPath '' $mapped) }
        $Spec = $mapped
    }
    $parts = $Spec.Split('/')
    $nameParts = if ($Spec.StartsWith('@')) { 2 } else { 1 }
    if ($parts.Count -lt $nameParts) { return $null }
    $name = $parts[0..($nameParts - 1)] -join '/'
    $subpath = ($parts | Select-Object -Skip $nameParts) -join '/'
    $key = Resolve-PackageKey $From.Key $name
    if (-not $key) {
        if ($Spec.StartsWith('node:') -or $NodeBuiltins -ccontains $name) { return $EmptyModule }
        return $null
    }
    $exports = Get-Field (Get-Container $key).Json 'exports'
    if ($null -ne $exports) {
        $target = Resolve-Exports $exports $(if ($subpath) { "./$subpath" } else { '.' }) $Kind
        if (-not $target) { return $null }
        return Resolve-File $key (Join-UrlPath '' $target)
    }
    if ($subpath) { return Resolve-File $key $subpath }
    return Resolve-Main $key ''
}

# True when $Spec is a bare import of a package that package $Key ('' = the app) declares in package-lock.json.
function Test-DeclaredDependency([string]$Key, [string]$Spec) {
    if ($Spec.StartsWith('.') -or $Spec.StartsWith('/')) { return $false }
    $parts = $Spec.Split('/')
    $name = if ($Spec.StartsWith('@') -and $parts.Count -gt 1) { $parts[0] + '/' + $parts[1] } else { $parts[0] }
    foreach ($field in 'dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies') {
        $deps = Get-Field $Lock[$Key] $field
        if ((Test-JsonObject $deps) -and $deps.ContainsKey($name)) { return $true }
    }
    return $false
}

function Get-Imports([string]$Path, [string]$Text, [bool]$InApp) {
    $extension = [IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -eq '.css') {
        foreach ($m in $CssImportPattern.Matches($Text)) { [pscustomobject]@{ Spec = $m.Groups[2].Value; Kind = 'import' } }
        foreach ($m in $CssUrlPattern.Matches($Text)) {
            $url = $m.Groups[2].Value -replace '[?#].*$', ''
            if ($url -and $url -notmatch '^([a-z]+:|/)') { [pscustomobject]@{ Spec = $(if ($url.StartsWith('.')) { $url } else { "./$url" }); Kind = 'import' } }
        }
    } elseif ($ScriptExtensions -contains $extension) {
        foreach ($m in $ImportPattern.Matches($Text)) {
            $spec = $m.Groups[2].Value
            # Quoted URLs after "from" in comments are not imports.
            if ($spec -match '^[a-z]+:' -and -not $spec.StartsWith('node:')) { continue }
            [pscustomobject]@{ Spec = $spec; Kind = $(if ($m.Value.StartsWith('require')) { 'require' } else { 'import' }) }
        }
        # Vite's React plugin compiles JSX to calls into react/jsx-runtime.
        if ($InApp -and ($extension -eq '.jsx' -or $extension -eq '.tsx')) { [pscustomobject]@{ Spec = 'react/jsx-runtime'; Kind = 'import' } }
    }
}

function Get-DeclaredLicence($PackageJson) {
    $license = Get-Field $PackageJson 'license'
    if (Test-JsonObject $license) { $license = Get-Field $license 'type' }
    if (-not $license) {
        $licenses = Get-Field $PackageJson 'licenses'
        if ($licenses -is [object[]]) { $license = (@($licenses | ForEach-Object { Get-Field $_ 'type' }) -join ' OR ') }
    }
    if ($license -is [string] -and $license -match '^\((.*)\)$') { $license = $Matches[1] }
    return $license
}

$goData = Get-Content $GoDataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$module = @($goData.modules | Where-Object { $_.path -eq $HtmlUiBuildModule })
if ($module.Count -ne 1) { throw "kopia-go-modules.json does not list $HtmlUiBuildModule." }
$module = $module[0]
Initialize-StandardApache $ApachePath

$ownCache = -not $CacheDir
if ($ownCache) { $CacheDir = Join-Path ([IO.Path]::GetTempPath()) ('cashgear-webui-packages-' + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
if (Test-Path $LicenceDir) { Remove-Item -Recurse -Force $LicenceDir }
New-Item -ItemType Directory -Path $LicenceDir | Out-Null

$problems = New-Object Collections.Generic.List[string]
$Containers = @{}
try {
    # 1. The htmluibuild module in kopia.exe, and the htmlui commit its build/index.html names.
    $zip = Read-ZipFiles (Get-Download ('https://proxy.golang.org/{0}/@v/{1}.zip' -f $HtmlUiBuildModule, $module.version))
    try {
        $hash = Get-ModuleZipHash $zip
        if ($hash -ne $module.sum) { throw "$HtmlUiBuildModule $($module.version): zip hash $hash, kopia-go-modules.json says $($module.sum)." }
        $indexEntry = $zip.GetEntry("$HtmlUiBuildModule@$($module.version)/build/index.html")
        if (-not $indexEntry) { throw "$HtmlUiBuildModule $($module.version) has no build/index.html." }
        $builtIndex = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $indexEntry))
    } finally { $zip.Dispose() }
    if ($builtIndex -notmatch 'kopia/htmlui ([0-9a-f]{40}) built on') { throw "build/index.html in $HtmlUiBuildModule $($module.version) does not name the kopia/htmlui commit." }
    $commit = $Matches[1]
    Write-Host "$HtmlUiBuildModule $($module.version) was built from kopia/htmlui $commit."

    # 2. The app's sources and lockfile at that commit, then the walk from index.html.
    $appFiles = New-Object 'Collections.Generic.Dictionary[string, byte[]]' ([StringComparer]::Ordinal)
    $zip = Read-ZipFiles (Get-Download "https://codeload.github.com/kopia/htmlui/zip/$commit")
    try {
        foreach ($entry in $zip.Entries) {
            $slash = $entry.FullName.IndexOf('/')
            if ($entry.FullName.EndsWith('/') -or $slash -lt 0) { continue }
            $appFiles[$entry.FullName.Substring($slash + 1)] = Read-EntryBytes $entry
        }
    } finally { $zip.Dispose() }
    $lockJson = Read-Json $appFiles['package-lock.json']
    if ((Get-Field $lockJson 'lockfileVersion') -lt 2) { throw 'kopia/htmlui has a package-lock.json older than lockfile version 2.' }
    $Lock = Get-Field $lockJson 'packages'
    $Containers[''] = [pscustomobject]@{ Key = ''; Name = 'htmlui'; Files = $appFiles; FileMap = @{}; ModuleMap = @{} }

    $reached = New-Object 'Collections.Generic.Dictionary[string, string]' ([StringComparer]::Ordinal)
    $visited = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $queue = New-Object 'Collections.Generic.Queue[object]'
    foreach ($m in [regex]::Matches([Text.Encoding]::UTF8.GetString($appFiles['index.html']), '<script\b[^>]*\bsrc="([^"]+)"')) {
        $entryFile = Resolve-File '' (Join-UrlPath '' $m.Groups[1].Value)
        if (-not $entryFile) { throw "index.html loads $($m.Groups[1].Value), which kopia/htmlui does not have." }
        $queue.Enqueue([pscustomobject]@{ File = $entryFile; From = 'the app' })
    }
    if ($queue.Count -eq 0) { throw 'index.html in kopia/htmlui loads no script.' }
    while ($queue.Count -gt 0) {
        $item = $queue.Dequeue()
        $file = $item.File
        if (-not $visited.Add("$($file.Key)|$($file.Path)")) { continue }
        $container = Get-Container $file.Key
        if ($file.Key -and -not $reached.ContainsKey($file.Key)) { $reached[$file.Key] = $item.From }
        $from = if ($file.Key) { "$($container.Name) $($container.Version)" } else { 'the app' }
        $text = [Text.Encoding]::UTF8.GetString($container.Files[$file.Path])
        foreach ($import in (Get-Imports $file.Path $text (-not $file.Key))) {
            $target = Resolve-Import $file $import.Spec $import.Kind
            if ($target) {
                if ($target.Key -ne $null) { $queue.Enqueue([pscustomobject]@{ File = $target; From = $from }) }
            } elseif (-not $file.Key -or (Test-DeclaredDependency $file.Key $import.Spec)) {
                $problems.Add("$from, $($file.Path): cannot resolve '$($import.Spec)'")
            } else {
                # The real build resolved every import it met, so one in a package naming neither a file nor a
                # dependency is text in a comment or string (example code in a doc comment or a warning message).
                Write-Host "  ignored: '$($import.Spec)' in $from, $($file.Path) (neither a file nor a dependency)"
            }
        }
    }

    # 3. Vite, whose module-preload polyfill is in the bundle.
    $buildToolKey = Resolve-PackageKey '' $BuildTool
    if (-not $buildToolKey) { throw "kopia/htmlui's package-lock.json has no $BuildTool." }
    $reached[$buildToolKey] = 'the build'

    # Dependencies of reached packages that the walk did not reach, for the maintainer to sanity-check.
    foreach ($key in (Sort-Ordinal $reached.Keys)) {
        $container = Get-Container $key
        if ($key -eq $buildToolKey) { continue }
        $entry = $Lock[$key]
        foreach ($field in 'dependencies', 'peerDependencies') {
            $deps = Get-Field $entry $field
            if (-not (Test-JsonObject $deps)) { continue }
            foreach ($dep in $deps.Keys) {
                $depKey = Resolve-PackageKey $key $dep
                if ($depKey -and -not $reached.ContainsKey($depKey)) { Write-Host "  not reached: $dep ($field of $($container.Name) $($container.Version))" }
            }
        }
    }

    # 4. Licence files, one package per name and version.
    $packages = @{}
    foreach ($key in $reached.Keys) {
        $container = Get-Container $key
        $id = "$($container.Name)@$($container.Version)"
        if (-not $packages.ContainsKey($id)) { $packages[$id] = [pscustomobject]@{ Container = $container; RequiredBy = $reached[$key] } }
    }
    $spdxFetched = @{}
    $records = foreach ($id in (Sort-Ordinal $packages.Keys)) {
        $container = $packages[$id].Container
        $label = "$($container.Name) $($container.Version)"
        $files = New-Object Collections.Generic.List[object]
        foreach ($path in (Sort-Ordinal $container.Files.Keys)) {
            if ($path.Contains('/') -or $path -notmatch $LicenceFilePattern) { continue }
            $bytes = $container.Files[$path]
            $text = [Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
            if ($container.Name -eq $BuildTool) {
                $end = $text.IndexOf($BuildToolLicenceEnd, [StringComparison]::Ordinal)
                if ($end -lt 0) { throw "$label ${path}: '$BuildToolLicenceEnd' not found." }
                $text = $text.Substring(0, $end).TrimEnd() + "`n"
                $bytes = $Utf8.GetBytes($text)
            }
            $file = New-FileRecord $path $text
            if (-not $file.Notice -and -not $file.License) { $problems.Add("${label}: licence not identified in $path") }
            $files.Add($file)
            if (-not $file.StandardApache) {
                $target = Join-Path $LicenceDir ("$id/$path" -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                [IO.File]::WriteAllBytes($target, $bytes)
            }
        }
        if (-not ($files | Where-Object { -not $_.Notice })) { $problems.Add("${label}: no licence file at the top of the package") }

        $declared = Get-DeclaredLicence $container.Json
        $spdx = @()
        if (-not $declared -or $declared -match '[()]' -or ($declared -match ' AND ' -and $declared -match ' OR ')) {
            $problems.Add("${label}: package.json licence '$declared' is not a plain SPDX id or AND/OR list")
        } else {
            $ids = @($declared -split ' (?:AND|OR) ')
            $inFiles = @($files | ForEach-Object { $_.Ids })
            $missing = @($ids | Where-Object { $inFiles -notcontains $_ })
            if ($declared -match ' OR ' -and $missing.Count -lt $ids.Count) { $missing = @() }
            foreach ($licence in $missing) {
                if ($LinkedLicences -notcontains $licence) { $problems.Add("${label}: package.json says $licence, its licence files do not contain it"); continue }
                if (-not $spdxFetched.ContainsKey($licence)) {
                    $text = [Text.Encoding]::UTF8.GetString((Get-Download "https://raw.githubusercontent.com/spdx/license-list-data/v$SpdxVersion/text/$licence.txt"))
                    if ((Get-SpdxIds $text) -notcontains $licence) { throw "The SPDX text of $licence is not recognised as $licence." }
                    New-Item -ItemType Directory -Force -Path (Join-Path $LicenceDir 'spdx') | Out-Null
                    [IO.File]::WriteAllText((Join-Path $LicenceDir "spdx\$licence.txt"), $text, $Utf8)
                    $spdxFetched[$licence] = $true
                }
                $spdx += $licence
            }
        }
        $copyright = @($files | ForEach-Object { $_.Copyright }) | Select-Object -Unique
        $lines = @(
            "    {",
            "      `"name`": $(ConvertTo-JsonString $container.Name),",
            "      `"version`": $(ConvertTo-JsonString $container.Version),",
            "      `"integrity`": $(ConvertTo-JsonString $container.Integrity),",
            "      `"requiredBy`": $(ConvertTo-JsonString $packages[$id].RequiredBy),",
            "      `"license`": $(ConvertTo-JsonString $declared),",
            "      `"copyright`": $(Format-StringArray $copyright),")
        if ($spdx.Count -gt 0) { $lines += "      `"spdx`": $(Format-StringArray $spdx)," }
        if ($container.Name -eq $BuildTool) { $lines += "      `"note`": $(ConvertTo-JsonString $BuildToolNote)," }
        $lines += "      `"files`": $(Format-Files $files '      ')"
        $lines += "    }"
        $lines -join "`n"
    }
} finally {
    if ($ownCache) { Remove-Item -Recurse -Force $CacheDir -ErrorAction SilentlyContinue }
}
if ($problems.Count -gt 0) { throw "Cannot finish, fix these first:`n  " + ($problems -join "`n  ") }

$json = New-Object Text.StringBuilder
[void]$json.Append("{`n")
[void]$json.Append("  `"schemaVersion`": 1,`n")
[void]$json.Append("  `"htmluibuild`": { `"path`": $(ConvertTo-JsonString $HtmlUiBuildModule), `"version`": $(ConvertTo-JsonString $module.version), `"sum`": $(ConvertTo-JsonString $module.sum) },`n")
[void]$json.Append("  `"htmlui`": { `"repository`": $(ConvertTo-JsonString $HtmlUiRepository), `"commit`": $(ConvertTo-JsonString $commit) },`n")
[void]$json.Append("  `"spdxLicenseListVersion`": $(ConvertTo-JsonString $SpdxVersion),`n")
[void]$json.Append("  `"packages`": [`n")
[void]$json.Append((@($records) -join ",`n")).Append("`n  ]`n}`n")
[IO.File]::WriteAllText($DataPath, $json.ToString(), $Utf8)
Write-Host "Wrote $DataPath ($(@($records).Count) packages) and $LicenceDir."
