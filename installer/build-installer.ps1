# Builds the CashGear Backup MSI into artifacts\installer.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.2.0]
#
# 1. Checks tools\kopia\kopia.exe against tools\kopia\kopia.pin.json (the .wixproj checks the copy it packs again).
# 2. Publishes the agent and the desktop app, self-contained for win-x64, into artifacts\publish.
# 3. Signs our own binaries when CASHGEAR_SIGN_CERT_THUMBPRINT is set (certificate in the current user's or the
#    machine's store; CASHGEAR_SIGN_TIMESTAMP_URL is then required; CASHGEAR_SIGNTOOL overrides where signtool is).
#    kopia.exe keeps its own publisher's signature and Microsoft's files keep Microsoft's.
# 4. Builds the MSI with WiX 5.0.2 and signs it the same way.

param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\installer'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# 1. The pinned engine.
$pin = Get-Content (Join-Path $root 'tools\kopia\kopia.pin.json') -Raw | ConvertFrom-Json
$engine = Join-Path $root 'tools\kopia\kopia.exe'
if (-not (Test-Path $engine)) { throw "$engine is missing: run tools\kopia\fetch-kopia.ps1 first." }
$hash = (Get-FileHash $engine -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $pin.exe.sha256.ToLowerInvariant()) { throw "$engine does not match kopia.pin.json; run tools\kopia\fetch-kopia.ps1 again." }

# 2. Publish.
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($app in @(@{ Project = 'src\CashGear.Backup.Agent'; Dir = 'agent' }, @{ Project = 'src\CashGear.Backup.Desktop'; Dir = 'desktop' })) {
    Invoke-Checked "Publishing $($app.Project)" {
        dotnet publish (Join-Path $root $app.Project) -c $Configuration -r win-x64 --self-contained true `
            -p:Version=$Version -o (Join-Path $publish $app.Dir) --nologo
    }
}

# 3. Signing (optional).
$thumbprint = $env:CASHGEAR_SIGN_CERT_THUMBPRINT
function Invoke-Sign([string[]]$files) {
    if (-not $thumbprint -or $files.Count -eq 0) { return }
    if (-not $env:CASHGEAR_SIGN_TIMESTAMP_URL) { throw 'CASHGEAR_SIGN_TIMESTAMP_URL is required with CASHGEAR_SIGN_CERT_THUMBPRINT (signatures must outlive the certificate).' }
    $signtool = $env:CASHGEAR_SIGNTOOL
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK or set CASHGEAR_SIGNTOOL.' }
    Invoke-Checked 'Signing' { & $signtool sign /sha1 $thumbprint /fd SHA256 /tr $env:CASHGEAR_SIGN_TIMESTAMP_URL /td SHA256 $files }
}
Invoke-Sign @(Get-ChildItem $publish -Recurse -Include 'CashGear.*.dll', 'CashGear.*.exe' | Select-Object -ExpandProperty FullName)

# 4. The MSI.
Invoke-Checked 'Building the MSI' {
    dotnet build (Join-Path $root 'installer\CashGear.Backup.Installer\CashGear.Backup.Installer.wixproj') -c $Configuration `
        -p:ProductVersion=$Version -p:PublishRoot="$publish\" -o $output --nologo
}
$msi = Join-Path $output "CashGearBackup-$Version.msi"
Invoke-Sign @($msi)
Write-Host "Built $msi"
