# Helpers shared by the maintainer tools that collect licence files for THIRD-PARTY-NOTICES.txt
# (collect-kopia-go-modules.ps1, collect-kopia-webui-packages.ps1). Dot-source it, then call
# Initialize-StandardApache before Test-StandardApache or New-FileRecord.
#
# This file must stay ASCII: Windows PowerShell 5.1 reads BOM-less files as ANSI.

$LicenceFilePattern = '^(LICEN[CS]E|COPYING|COPYRIGHT|NOTICE|UNLICENSE)([._-].*)?$'

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($Bytes) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}

# golang.org/x/mod/sumdb/dirhash Hash1: SHA-256 over "<sha256 hex>  <name>\n" for every file, names sorted.
function Get-ModuleZipHash([IO.Compression.ZipArchive]$Zip) {
    $names = [string[]]@($Zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $summary = New-Object Text.StringBuilder
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($name in $names) {
            $stream = $Zip.GetEntry($name).Open()
            try { $hash = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
            [void]$summary.Append((($hash | ForEach-Object { $_.ToString('x2') }) -join '')).Append('  ').Append($name).Append("`n")
        }
        return 'h1:' + [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($summary.ToString())))
    } finally { $sha.Dispose() }
}

function Read-EntryBytes([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    try { $ms = New-Object IO.MemoryStream; $stream.CopyTo($ms); return , $ms.ToArray() } finally { $stream.Dispose() }
}

# SPDX ids of the licences a file contains (some files concatenate several), in the order they appear, from phrases
# each licence's text always contains. Empty when none matches.
function Get-SpdxIds([string]$Text) {
    $t = $Text.ToLowerInvariant() -replace '[\s#/*>]+', ' '
    $bsd = 'redistribution and use in source and binary forms, with or without modification, are permitted'
    $signatures = @(
        @('Apache-2.0', 'apache license version 2.0'), @('Apache-2.0', 'apache license, version 2.0'),
        @('MPL-2.0', 'mozilla public license version 2.0'), @('MPL-2.0', 'mozilla public license, version 2.0'),
        @('MIT', 'permission is hereby granted, free of charge, to any person obtaining a copy'),
        @('ISC', 'permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted'),
        @('ISC', 'permission to use, copy, modify, and distribute this software for any purpose with or without fee is hereby granted'),
        @('CC0-1.0', 'cc0 1.0 universal'),
        @('CC-BY-4.0', 'creative commons attribution 4.0 international public license'),
        @('OFL-1.1', 'sil open font license version 1.1'),
        @('Unlicense', 'this is free and unencumbered software released into the public domain')
    )
    $found = foreach ($s in $signatures) {
        $i = $t.IndexOf($s[1], [StringComparison]::Ordinal)
        if ($i -ge 0) {
            $id = $s[0]
            # 0BSD is the ISC grant without the condition that the notice appear in all copies.
            if ($id -eq 'ISC' -and -not $t.Substring($i + $s[1].Length).StartsWith(', provided that', [StringComparison]::Ordinal)) { $id = '0BSD' }
            [pscustomobject]@{ Id = $id; At = $i }
        }
    }
    # A BSD text is 3-clause when the no-endorsement clause follows it before the next BSD text starts.
    $i = $t.IndexOf($bsd, [StringComparison]::Ordinal)
    while ($i -ge 0) {
        $next = $t.IndexOf($bsd, $i + 1, [StringComparison]::Ordinal)
        $end = if ($next -ge 0) { $next } else { $t.Length }
        $endorse = $t.IndexOf('endorse or promote products derived from this software', $i, [StringComparison]::Ordinal)
        $found = @($found) + [pscustomobject]@{ Id = $(if ($endorse -ge 0 -and $endorse -lt $end) { 'BSD-3-Clause' } else { 'BSD-2-Clause' }); At = $i }
        $i = $next
    }
    return @($found | Where-Object { $_ } | Sort-Object At | ForEach-Object { $_.Id } | Select-Object -Unique)
}

# Copyright lines as the file writes them: lines starting "Copyright <year or name>", or any line with "(c) <year>".
# Skips the Apache appendix template and licence prose that merely mentions copyright.
function Get-CopyrightLines([string]$Text) {
    $lines = foreach ($line in $Text.Split("`n")) {
        $l = ($line.Trim() -replace '\s+', ' ')
        if (($l -cmatch '^(Copyright|COPYRIGHT) (\([cC]\)|\xA9|[0-9A-Z])' -or $l -cmatch '(\([cC]\)|\xA9) ?[0-9]{4}') -and
            $l -notmatch '\[yyyy\]|\{yyyy\}|copyright notice|copyright holder|copyright owner|copyright license') { $l }
    }
    return @($lines | Select-Object -Unique)
}

# Letters and digits only, so line wrapping and punctuation do not matter when comparing licence texts.
function Get-Skeleton([string]$Text) { return ($Text.ToLowerInvariant() -replace '[^a-z0-9]+', '') }

# What Apache-2.0 LICENSE files usually carry after the terms: the "how to apply" appendix, or just its notice.
$ApacheNotice = 'Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in
compliance with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on
an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.'
$ApacheAppendix = 'APPENDIX: How to apply the Apache License to your work. To apply the Apache License to your work,
attach the following boilerplate notice, with the fields enclosed by brackets "[]" replaced with your own identifying
information. (Don''t include the brackets!) The text should be enclosed in the appropriate comment syntax for the
file format. We also recommend that a file or class name and description of purpose be included on the same
"printed page" as the copyright notice for easier identification within third-party archives.'

# Reads the Apache-2.0 text that section 4 of the notices prints (tools\kopia\LICENSE) for Test-StandardApache.
function Initialize-StandardApache([string]$Path) {
    $apacheText = [IO.File]::ReadAllText($Path)
    $script:ApacheTerms = Get-Skeleton $apacheText.Substring(0, $apacheText.IndexOf('END OF TERMS AND CONDITIONS', [StringComparison]::Ordinal))
    $script:ApacheEndings = @('', (Get-Skeleton $ApacheNotice), (Get-Skeleton "$ApacheAppendix $ApacheNotice"))
}

# True when the file holds the same Apache-2.0 terms as tools\kopia\LICENSE (which section 4 of the notices prints),
# followed by nothing but copyright lines and the usual appendix or notice. Such files are not repeated.
function Test-StandardApache([string]$Text) {
    $marker = 'END OF TERMS AND CONDITIONS'
    $at = $Text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
    if ($at -lt 0 -or (Get-Skeleton $Text.Substring(0, $at)) -ne $script:ApacheTerms) { return $false }
    $rest = foreach ($line in $Text.Substring($at + $marker.Length).Split("`n")) {
        if (-not (Get-CopyrightLines $line) -and $line -notmatch 'copyright\s*[\[{]yyyy') { $line }
    }
    return $script:ApacheEndings -contains (Get-Skeleton ($rest -join "`n"))
}

function ConvertTo-JsonString([string]$Value) { return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"' }

function Format-StringArray($Values) { return '[' + ((@($Values | Where-Object { $_ }) | ForEach-Object { ConvertTo-JsonString $_ }) -join ', ') + ']' }

function Format-Files($Files, [string]$Indent) {
    $items = foreach ($f in $Files) {
        $fields = @('"path": ' + (ConvertTo-JsonString $f.Path))
        if ($f.Notice) { $fields += '"notice": true' } else { $fields += '"license": ' + (ConvertTo-JsonString $f.License) }
        if ($f.StandardApache) { $fields += '"standardApache": true' }
        "$Indent  { " + ($fields -join ', ') + ' }'
    }
    return "[`n" + ($items -join ",`n") + "`n$Indent]"
}

# Reads one licence or notice file into the record the JSON keeps; $null license = not identified.
function New-FileRecord([string]$Path, [string]$Text) {
    $notice = (Split-Path $Path -Leaf) -match '^NOTICE'
    $ids = @(if (-not $notice) { Get-SpdxIds $Text })
    return [pscustomobject]@{
        Path = $Path
        Notice = $notice
        License = if ($ids.Count -gt 0) { $ids -join ' AND ' } else { $null }
        Ids = $ids
        StandardApache = (-not $notice) -and ($ids -join ',') -eq 'Apache-2.0' -and (Test-StandardApache $Text)
        Copyright = Get-CopyrightLines $Text
    }
}
