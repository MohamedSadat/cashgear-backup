# Regenerates THIRD-PARTY-NOTICES.txt (repo root) from what the agent and the desktop app ship: the pinned
# kopia.exe and every NuGet package with runtime files. Run after a restore or build, whenever a package or the
# kopia pin changes; NoticesTests fails until the file is current.
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
[void]$out.AppendLine('  kopia.exe also contains Go modules under their own licences; their notices are listed in')
[void]$out.AppendLine('  section 3 once collected (open item before release, see installer\CashGear.Backup.Installer\README.md).')
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
[void]$out.AppendLine('Not yet collected.')
[void]$out.AppendLine()
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

$target = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
[System.IO.File]::WriteAllText($target, $out.ToString().Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $target ($($rows.Count) packages)."
