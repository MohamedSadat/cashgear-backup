# CashGear Backup

Windows file-backup product: a .NET 10 Windows Service ("agent") and a WPF + Blazor desktop app,
with [Kopia](https://github.com/kopia/kopia) (Apache-2.0) bundled as the backup engine.

- Plan: [docs/plan](docs/plan/) (snapshot of the live plan doc)
- Rules for contributors and coding agents: [CLAUDE.md](CLAUDE.md)

## First build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1
dotnet build CashGear.Backup.slnx
dotnet test --solution CashGear.Backup.slnx
```
