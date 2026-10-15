# Regenerates THIRD-PARTY-NOTICES.txt (repo root) from what the agent and the desktop app ship: the pinned
# kopia.exe, the Go modules inside it (tools\notices\kopia-go-modules.json and kopia-go-licenses, written by
# collect-kopia-go-modules.ps1), the npm packages in Kopia's web UI inside it (kopia-webui-packages.json and
# kopia-webui-licenses, written by collect-kopia-webui-packages.ps1) and every NuGet package with runtime files. Run
# after a restore or build, whenever a package or the kopia pin changes (for a new pin, run
# collect-kopia-go-modules.ps1 and then collect-kopia-webui-packages.ps1 first); NoticesTests fails until the file is
# current. Reads only local files.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\notices\update-notices.ps1

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$projects = @('src\CashGear.Backup.Agent', 'src\CashGear.Backup.Desktop')
$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

function Test-RuntimeAssets($entry) {
    foreach ($kind in 'runtime', 'runtimeTargets', 'native') {
        $assets = $entry.$kind
        if ($assets) {
            foreach ($name in $assets.PSObject.Properties.Name) {
                if (-not $name.EndsWith('_._')) { return $true }
            }
        }
    }
    return $false
}

$packages = @{}
foreach ($project in $projects) {
    $assetsFile = Join-Path $root "$project\obj\project.assets.json"
    if (-not (Test-Path $assetsFile)) { throw "$assetsFile is missing: run 'dotnet restore CashGear.Backup.slnx' first." }
    $assets = Get-Content $assetsFile -Raw | ConvertFrom-Json
    foreach ($target in $assets.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            if ($library.Value.type -eq 'package' -and (Test-RuntimeAssets $library.Value)) {
                $packages[$library.Name] = $true
            }
        }
    }
}

$licenceFiles = @()
$rows = foreach ($key in ($packages.Keys | Sort-Object)) {
    $id, $version = $key.Split('/')
    $nuspec = Join-Path $packagesRoot ("{0}\{1}\{0}.nuspec" -f $id.ToLowerInvariant(), $version.ToLowerInvariant())
    [xml]$spec = Get-Content $nuspec -Raw
    $metadata = $spec.package.metadata
    $license = if ($metadata.license -and $metadata.license.type -eq 'expression') { $metadata.license.'#text' }
               elseif ($metadata.license) {
                   $licenceFiles += [pscustomobject]@{ Id = $id; Text = (Get-Content (Join-Path (Split-Path $nuspec) $metadata.license.'#text') -Raw).TrimEnd() }
                   "its own licence, full text in section 4" }
               else { $metadata.licenseUrl }
    [pscustomobject]@{
        Id = $id; Version = $version; License = $license
        Copyright = if ($metadata.copyright) { $metadata.copyright } else { '(no copyright line in the package)' }
        Url = if ($metadata.projectUrl) { $metadata.projectUrl } else { "https://www.nuget.org/packages/$id/$version" }
    }
}

$pin = Get-Content (Join-Path $root 'tools\kopia\kopia.pin.json') -Raw | ConvertFrom-Json
$apache = (Get-Content (Join-Path $root 'tools\kopia\LICENSE') -Raw).TrimEnd()

$goData = Get-Content (Join-Path $PSScriptRoot 'kopia-go-modules.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($goData.kopiaVersion -ne $pin.version -or $goData.kopiaExeSha256 -ne $pin.exe.sha256) {
    throw "kopia-go-modules.json describes Kopia $($goData.kopiaVersion), the pin is $($pin.version): run tools\notices\collect-kopia-go-modules.ps1 first."
}
$goVersion = $goData.go.version -replace '^go', ''
$goEntries = @([pscustomobject]@{
    Title = "Go $goVersion standard library and runtime"; Label = "Go $goVersion"; Folder = 'std'; Item = $goData.go
    Source = "https://go.googlesource.com/go/+/refs/tags/$($goData.go.version)"
}) + @(foreach ($module in $goData.modules) {
    [pscustomobject]@{
        Title = "$($module.path) $($module.version)"; Label = "$($module.path) $($module.version)"; Folder = $module.path
        Item = $module; Source = "https://pkg.go.dev/$($module.path)@$($module.version)"
    }
})

# Licence and NOTICE texts of the Go modules, each distinct text once with the files that carry it. Files that are
# the Apache-2.0 text of section 4 (standardApache) are not repeated.
$goTexts = New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)
foreach ($entry in $goEntries) {
    foreach ($file in $entry.Item.files) {
        if ($file.PSObject.Properties['standardApache']) { continue }
        $path = Join-Path $PSScriptRoot ("kopia-go-licenses\$($entry.Folder)\$($file.path)" -replace '/', '\')
        $lines = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Replace("`r`n", "`n").Split("`n") | ForEach-Object { $_.TrimEnd() }
        $text = ($lines -join "`n").Trim("`n")
        if (-not $goTexts.Contains($text)) { $goTexts.Add($text, (New-Object System.Collections.Generic.List[string])) }
        $goTexts[$text].Add("$($entry.Label): $($file.path)")
    }
}

# The npm packages compiled into Kopia's web UI (github.com/kopia/htmluibuild, one of the Go modules above).
$webData = Get-Content (Join-Path $PSScriptRoot 'kopia-webui-packages.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$htmlUiBuild = @($goData.modules | Where-Object { $_.path -eq $webData.htmluibuild.path })
if ($htmlUiBuild.Count -ne 1 -or $htmlUiBuild[0].version -ne $webData.htmluibuild.version -or $htmlUiBuild[0].sum -ne $webData.htmluibuild.sum) {
    throw "kopia-webui-packages.json describes $($webData.htmluibuild.path) $($webData.htmluibuild.version), kopia-go-modules.json does not: run tools\notices\collect-kopia-webui-packages.ps1 first."
}
$webTexts = New-Object System.Collections.Specialized.OrderedDictionary ([StringComparer]::Ordinal)
foreach ($package in $webData.packages) {
    $label = "$($package.name) $($package.version)"
    $sources = @(foreach ($file in $package.files) {
        if (-not $file.PSObject.Properties['standardApache']) { [pscustomobject]@{ Path = "$($package.name)@$($package.version)\$($file.path)"; Label = "${label}: $($file.path)" } }
    })
    if ($package.PSObject.Properties['spdx']) {
        $sources += @(foreach ($id in $package.spdx) { [pscustomobject]@{ Path = "spdx\$id.txt"; Label = "${label}: $id (text from the SPDX licence list $($webData.spdxLicenseListVersion))" } })
    }
    foreach ($source in $sources) {
        $path = Join-Path $PSScriptRoot ("kopia-webui-licenses\$($source.Path)" -replace '/', '\')
        $lines = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8).Replace("`r`n", "`n").Split("`n") | ForEach-Object { $_.TrimEnd() }
        $text = ($lines -join "`n").Trim("`n")
        if (-not $webTexts.Contains($text)) { $webTexts.Add($text, (New-Object System.Collections.Generic.List[string])) }
        $webTexts[$text].Add($source.Label)
    }
}
$mit = @'
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
'@
$bsd3 = @'
BSD 3-Clause License

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
   following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
   products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE
GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
'@

$out = New-Object System.Text.StringBuilder
[void]$out.AppendLine('CashGear Backup - third-party notices')
[void]$out.AppendLine('=====================================')
[void]$out.AppendLine()
[void]$out.AppendLine('CashGear Backup includes the open-source software listed below. Each part is used under its own licence;')
[void]$out.AppendLine('the licence texts follow the list. This file is generated by tools\notices\update-notices.ps1.')
[void]$out.AppendLine()
[void]$out.AppendLine('1. Backup engine')
[void]$out.AppendLine('----------------')
[void]$out.AppendLine()
[void]$out.AppendLine("Kopia $($pin.version) (kopia.exe, unmodified release binary)")
[void]$out.AppendLine("  Licence: $($pin.license), full text in section 4")
[void]$out.AppendLine('  Copyright: The Kopia Authors')
[void]$out.AppendLine('  Source: https://github.com/kopia/kopia')
[void]$out.AppendLine('  kopia.exe also contains the Go standard library and Go modules under their own licences, listed in')
[void]$out.AppendLine("  section 3, and Kopia's web UI with the npm packages compiled into it, listed in section 6.")
[void]$out.AppendLine()
[void]$out.AppendLine('2. .NET runtime and packages')
[void]$out.AppendLine('----------------------------')
[void]$out.AppendLine()
[void]$out.AppendLine('.NET runtime, ASP.NET Core runtime and Windows Desktop runtime (included in the app folders)')
[void]$out.AppendLine('  Licence: MIT')
[void]$out.AppendLine('  Copyright: .NET Foundation and Contributors')
[void]$out.AppendLine('  Source: https://github.com/dotnet')
[void]$out.AppendLine()
foreach ($row in $rows) {
    [void]$out.AppendLine("$($row.Id) $($row.Version)")
    [void]$out.AppendLine("  Licence: $($row.License)")
    [void]$out.AppendLine("  Copyright: $($row.Copyright)")
    [void]$out.AppendLine("  Source: $($row.Url)")
    [void]$out.AppendLine()
}
[void]$out.AppendLine('3. Go modules in kopia.exe')
[void]$out.AppendLine('--------------------------')
[void]$out.AppendLine()
[void]$out.AppendLine("kopia.exe (Kopia $($pin.version)) contains the Go $goVersion standard library and runtime and the $(@($goData.modules).Count) Go modules below,")
[void]$out.AppendLine('as recorded in the build information inside the binary. Their licence and NOTICE files are reproduced in')
[void]$out.AppendLine('section 5, except files that hold the Apache-2.0 text of section 4.')
[void]$out.AppendLine()
foreach ($entry in $goEntries) {
    $copyright = @($entry.Item.copyright)
    if ($copyright.Count -eq 0) { $copyright = @("(none in the module's licence files)") }
    [void]$out.AppendLine($entry.Title)
    [void]$out.AppendLine("  Licence: $($entry.Item.license)")
    [void]$out.AppendLine("  Copyright: $($copyright[0])")
    foreach ($line in ($copyright | Select-Object -Skip 1)) { [void]$out.AppendLine("             $line") }
    [void]$out.AppendLine("  Source: $($entry.Source)")
    if ($entry.Folder -eq $webData.htmluibuild.path) { [void]$out.AppendLine("  Contains Kopia's web UI; the npm packages compiled into it are listed in section 6.") }
    [void]$out.AppendLine()
}
[void]$out.AppendLine('4. Licence texts')
[void]$out.AppendLine('----------------')
[void]$out.AppendLine()
[void]$out.AppendLine($apache)
[void]$out.AppendLine()
[void]$out.AppendLine('----')
[void]$out.AppendLine()
[void]$out.AppendLine($mit.TrimEnd())
[void]$out.AppendLine()
[void]$out.AppendLine('----')
[void]$out.AppendLine()
[void]$out.AppendLine($bsd3.TrimEnd())
foreach ($file in $licenceFiles) {
    [void]$out.AppendLine()
    [void]$out.AppendLine('----')
    [void]$out.AppendLine()
    [void]$out.AppendLine("$($file.Id):")
    [void]$out.AppendLine()
    [void]$out.AppendLine($file.Text)
}
[void]$out.AppendLine()
[void]$out.AppendLine('5. Go module licence and notice files')
[void]$out.AppendLine('-------------------------------------')
foreach ($text in $goTexts.Keys) {
    [void]$out.AppendLine()
    [void]$out.AppendLine('----')
    [void]$out.AppendLine()
    foreach ($label in $goTexts[$text]) { [void]$out.AppendLine($label) }
    [void]$out.AppendLine()
    [void]$out.AppendLine($text)
}
[void]$out.AppendLine()
[void]$out.AppendLine("6. npm packages in Kopia's web UI")
[void]$out.AppendLine('---------------------------------')
[void]$out.AppendLine()
[void]$out.AppendLine("kopia.exe also contains Kopia's web UI ($($webData.htmluibuild.path) in section 3), a Vite build of")
[void]$out.AppendLine("$($webData.htmlui.repository) at commit $($webData.htmlui.commit). CashGear Backup does not")
[void]$out.AppendLine("serve it, but it ships inside kopia.exe. The build compiles in the $(@($webData.packages).Count) npm packages below and drops their")
[void]$out.AppendLine('licence comments, so their licence and NOTICE files are reproduced in section 7, except files that hold the')
[void]$out.AppendLine('Apache-2.0 text of section 4.')
[void]$out.AppendLine()
foreach ($package in $webData.packages) {
    $copyright = @($package.copyright)
    if ($copyright.Count -eq 0) { $copyright = @("(none in the package's licence files)") }
    [void]$out.AppendLine("$($package.name) $($package.version)")
    [void]$out.AppendLine("  Licence: $($package.license)")
    [void]$out.AppendLine("  Copyright: $($copyright[0])")
    foreach ($line in ($copyright | Select-Object -Skip 1)) { [void]$out.AppendLine("             $line") }
    [void]$out.AppendLine("  Source: https://www.npmjs.com/package/$($package.name)/v/$($package.version)")
    if ($package.PSObject.Properties['note']) { [void]$out.AppendLine("  Note: $($package.note)") }
    [void]$out.AppendLine()
}
[void]$out.AppendLine("7. Web UI package licence and notice files")
[void]$out.AppendLine('------------------------------------------')
foreach ($text in $webTexts.Keys) {
    [void]$out.AppendLine()
    [void]$out.AppendLine('----')
    [void]$out.AppendLine()
    foreach ($label in $webTexts[$text]) { [void]$out.AppendLine($label) }
    [void]$out.AppendLine()
    [void]$out.AppendLine($text)
}

$target = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
[System.IO.File]::WriteAllText($target, $out.ToString().Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $target ($($rows.Count) packages, $(@($goData.modules).Count) Go modules, $(@($webData.packages).Count) web UI packages)."
