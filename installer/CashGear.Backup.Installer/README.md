# CashGear.Backup.Installer

The CashGear Backup MSI, built with **WiX 5.0.2** (chosen because later WiX versions only build after accepting
the Open Source Maintenance Fee EULA; nobody has accepted that for CashGear, and a coding agent must not).
It is not part of `CashGear.Backup.slnx`: it packs the published apps.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1      # once per clone
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.0
dotnet test --project tests\CashGear.Backup.Installer.Tests                          # reads the MSI's tables
```

The script checks `tools\kopia\kopia.exe` against the pin, publishes the agent and the desktop app
self-contained for win-x64 into `artifacts\publish`, and builds `artifacts\installer\CashGearBackup-<version>.msi`.
The `.wixproj` checks the `kopia.exe` it packs against `tools\kopia\kopia.pin.json` again.

## What the MSI does

| | |
|---|---|
| Scope | Per machine, x64, Windows 10 or later. Major upgrades in place (`UpgradeCode` F1AE5C97-…); downgrades refused. |
| Programs | `C:\Program Files\CashGear Backup\Agent` (service, `kopia.exe`, `appsettings.json`) and `…\App` (desktop app, `THIRD-PARTY-NOTICES.txt`). |
| Service | `CashGearBackupAgent`, LocalSystem, automatic (delayed) start, restarted 60 s after each failure (counter reset daily). Stopped before files change, started after install, removed on uninstall. Delayed start is set with `sc.exe` right after `InstallServices` (WiX warns that the MSI `MsiServiceConfig` table is unreliable). |
| Data folder | `C:\ProgramData\CashGear\Backup`, created with SDDL `O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)`: SYSTEM and Administrators only, not inherited from ProgramData. |
| Desktop | Start menu shortcut; `HKLM\…\Run` starts the app with `--tray` at every sign-in. |
| Event log | Source "CashGear Backup" in the Application log. |
| Uninstall | Removes programs, service, shortcut and Run entry. **Leaves the data folder** (settings, state, logs) and never touches backups (they are at their destinations). Delete `C:\ProgramData\CashGear\Backup` by hand to remove every trace. |

## Silent install

```powershell
msiexec /i CashGearBackup-1.0.0.msi /qn /norestart
msiexec /i CashGearBackup-1.0.0.msi /qn /norestart LICENCEKEY=XXXX-XXXX-XXXX
msiexec /x CashGearBackup-1.0.0.msi /qn
```

`LICENCEKEY` is a hidden property (kept out of the installer log). It is written to `licence.ini` in the data
folder, which the agent activates with the licence server at its next start and then deletes.

## Signing

Set `CASHGEAR_SIGN_CERT_THUMBPRINT` (SHA-1 thumbprint of the code-signing certificate in a certificate store) and
`CASHGEAR_SIGN_TIMESTAMP_URL` (RFC 3161 server) before running the script; `CASHGEAR_SIGNTOOL` overrides where
`signtool.exe` is found (default: newest Windows 10 SDK). It signs `CashGear.*.dll`/`.exe` before packing and the
MSI after. `kopia.exe` keeps its own publisher's signature and Microsoft's files keep Microsoft's. The agent only
installs updates signed by a certificate whose SHA-256 thumbprint is in `Agent:UpdatePublisherThumbprints`, so
that list must name this certificate.

## Before shipping (open)

- **Install on a clean VM** (Windows 10 and 11): install, upgrade over an older version, `LICENCEKEY`, uninstall,
  service recovery and delayed start. Only the MSI's tables are tested here; it has not been installed.
- **WebView2 Runtime**: the desktop window needs it (present on Windows 11 and current Windows 10). The MSI does not
  check for it or install it; the agent works without it.
- **Code signing** is wired but has not run (no certificate here).
- **Production configuration**: `Agent:LicenceServerUrl`, `LicencePublicKeys`, `UpdateManifestUrl`,
  `UpdatePublicKeys` and `UpdatePublisherThumbprints` in the agent's `appsettings.json` are empty, so licences
  cannot be activated (Free edition) and no updates are checked.
- **Go module notices** for `kopia.exe` are still missing from `THIRD-PARTY-NOTICES.txt`
  (`tools\notices\update-notices.ps1` lists the .NET side).
