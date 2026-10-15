# CashGear.Backup.Installer (Phase 4 placeholder)

The MSI is built in Phase 4. Per the plan, it installs the agent service (`CashGearBackupAgent`,
automatic delayed start, restart on failure), the desktop app and the pinned `kopia.exe` together. It
creates the ProgramData folders with SYSTEM/Administrators ACLs, is per-machine only, supports silent
switches, and never deletes backup repositories on uninstall.

This project is deliberately not in `CashGear.Backup.slnx` yet.

## Decision needed before Phase 4: which WiX

| Option | Notes |
|---|---|
| WiX v7 (`WixToolset.Sdk` 7.x) | Builds only after accepting the Open Source Maintenance Fee EULA (`<AcceptEula>wix7</AcceptEula>`). Organisations above the revenue threshold (about $10k/year) are asked to sponsor the WiX project. See [OSMF](https://docs.firegiant.com/wix/osmf/). |
| WiX 5.0.2 | Predates the fee. Older, but supports what we need (service install, ACLs, custom actions). |

Whoever owns licensing for CashGear makes this call; a coding agent must not accept the EULA on
anyone's behalf.

## Also before shipping

- Ship Kopia's `LICENSE` and the licences of the Go modules compiled into `kopia.exe`
  (see `tools/kopia/README.md`).
- Authenticode-sign our binaries and the MSI; re-verify `kopia.exe` against `tools/kopia/kopia.pin.json`
  at build time.
