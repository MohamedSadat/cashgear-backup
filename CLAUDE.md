# CashGear Backup — guide for contributors and coding agents

Windows file-backup product. Kopia (Go, Apache-2.0) is the bundled backup engine; everything the
customer sees is .NET 10 C#.

- **Plan (source of truth):** the live doc
  [Cash Gear Backup — Implementation Plan](https://claude.ai/artifact/TH8E7hrofjqwptoiV1wZKf).
  Snapshot: [docs/plan/2026-10-15-cashgear-backup-plan.md](docs/plan/2026-10-15-cashgear-backup-plan.md).
- **Verified Kopia behaviour:** [spikes/KopiaSpike/FINDINGS.md](spikes/KopiaSpike/FINDINGS.md). Where it
  disagrees with the plan, FINDINGS wins (it was observed against the pinned binary).
- **Status:** Foundation, Phase 0 and Phase 1a (Kopia engine behind `IBackupEngine`) done; see
  "Phase 1a" in FINDINGS. Next: Phase 1b, the agent: Windows Service host, SQLite/EF Core, Quartz,
  gRPC over a named pipe. Open decisions for it are listed at the end of FINDINGS.

## Architecture

```
Desktop (WPF + BlazorWebView, runs as the user)
   │  gRPC over a named pipe (no TCP port)
   ▼
Agent (Windows Service, LocalSystem): Quartz, SQLite/EF Core state.db, DPAPI secrets, Serilog
   │  HTTPS to 127.0.0.1:<random port>, pinned self-signed cert, random Basic-auth users, CSRF token
   ▼
kopia.exe server (one process per repository, started and stopped by the agent)
   ▼
Destination: local disk, SMB, SFTP, S3-compatible
```

Only `CashGear.Backup.Engine.Kopia` knows Kopia exists. The rest of the product talks to
`IBackupEngine` (in Core); `KopiaBackupEngine` implements it.

## Hard rules

1. **Kopia stays behind Engine.Kopia.** No other project under `src/` names Kopia; the Agent only
   references the project and registers it. `tests/CashGear.Backup.Core.Tests/ArchitectureTests.cs`
   enforces this. `src/` never references anything under `spikes/`.
2. **No secrets in logs, command lines, exceptions or test output.** That covers repository
   passwords, Kopia server/control credentials, DPAPI blobs, `Authorization` headers and `KOPIA_*`
   values. Redact any JSON `password` field before writing a request anywhere.
3. **Async everywhere.** No `.Result`, `.Wait()` or `GetAwaiter().GetResult()`; pass
   `CancellationToken` all the way down.
4. **Restore never overwrites by default.** All `overwrite*` flags false, target is a new empty
   folder, and always send `restoreDirEntryAtDepth = int.MaxValue` (Kopia's REST default of 0 writes
   `.kopia-entry` placeholder files instead of real files).
5. **Never mark a run Succeeded** unless there is exactly one new snapshot ID, `summary.numFailed == 0`,
   `summary.numIgnoredErrors == 0` and no `incomplete` flag. Kopia's server reports task SUCCESS even
   when files failed and when the run was canceled (`incomplete: "canceled"`). The rule lives in
   `Runs/BackupRunClassifier.cs`.
6. **Only the pinned `tools/kopia/kopia.exe`**, verified by SHA-256 against `tools/kopia/kopia.pin.json`.
   Never a kopia from PATH, never a user-supplied one.
7. **Launching `kopia server start`** (see FINDINGS for why each flag is needed):
   - `--ui --html=<stub dir>`: `--no-ui` also removes the REST API.
   - `--address=127.0.0.1:0`, then read `SERVER ADDRESS:` from stderr.
   - Our own cert via `--tls-cert-file/--tls-key-file`; pin its SHA-256 in `HttpClient`.
   - `--no-grpc --shutdown-on-stdin` (keep stdin open), `--no-persist-credentials
     --no-use-credential-manager`, `--cache-directory` and `--log-dir` inside our data folder.
   - Credentials only via env vars: `KOPIA_SERVER_USERNAME/PASSWORD`,
     `KOPIA_SERVER_CONTROL_USER/PASSWORD`, `KOPIA_PASSWORD`; set `KOPIA_CHECK_FOR_UPDATES=false`.
     Start from an environment with every inherited `KOPIA_*` variable removed.
8. **Write the whole Kopia policy for every source** (manual scheduling, compression, retention, VSS).
   Kopia's defaults do not compress and *do* prune with their own retention. `PUT /policy` replaces the
   whole policy and silently drops unknown fields, so always read it back (`KopiaPolicyMapper.Differences`).
9. **After `repo/create` or `repo/connect`, poll `/repo/status`**: the create response says
   `connected:false` even when it succeeded.
10. **Hand review** anything touching passwords, deletion, retention or restore overwrite. Agent
    mistakes there cost customer data.

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1   # once per clone
dotnet build CashGear.Backup.slnx
dotnet test --solution CashGear.Backup.slnx
dotnet test --solution spikes\KopiaSpike.slnx          # Phase 0 gate: real kopia round trip
dotnet run --project spikes\KopiaSpike -- --keep-workspace
```

Tests that need Kopia find it via `CASHGEAR_KOPIA_EXE` or `tools/kopia/kopia.exe` and fail with the
fetch command if it is missing or does not match the pin (a new git worktree needs the fetch too). The
engine tests take about a minute. To debug them, set `CASHGEAR_TEST_LOG_DIR` (one engine log per test,
passing tests included) and `CASHGEAR_KEEP_TEST_WORKSPACE=1` (keep `%TEMP%\cashgear-tests`).

## Repo map

| Path | What |
|---|---|
| `src/CashGear.Backup.Core` | Domain types and `IBackupEngine` (engine-agnostic) |
| `src/CashGear.Backup.Engine.Kopia` | The only Kopia-aware code: `KopiaBackupEngine`; `Api/` REST client and DTOs, `Hosting/` process host, pin, Job Object, `Policy/` job-to-policy mapping, `Runs/` run tracking and the rule-5 classifier |
| `src/CashGear.Backup.Agent` | Windows Service: scheduler, gRPC server, notifications, updater |
| `src/CashGear.Backup.Data` | EF Core + SQLite state |
| `src/CashGear.Backup.Contracts` | `.proto` files and generated gRPC types |
| `src/CashGear.Backup.UI.Components` | Razor class library: pages, wizards, file browser |
| `src/CashGear.Backup.Desktop` | WPF host: BlazorWebView, tray icon |
| `installer/` | WiX MSI (Phase 4; placeholder for now) |
| `tools/kopia/` | Kopia pin, fetch script, LICENSE (exe is gitignored) |
| `tests/` | xUnit v3 test projects; `tests/Shared` holds helpers linked into several projects (pinned binary, tree fixture and byte-compare manifest) |
| `tests/CashGear.Backup.Engine.Kopia.IntegrationTests` | `Contract/` one test per Kopia endpoint, `Engine/` round trips and data-safety rules through `IBackupEngine`, `Hosting/` process host, `Unit/` no-Kopia checks |
| `spikes/KopiaSpike` | Phase 0 throwaway spike and its tests; lift proven code into Engine.Kopia, then delete |
| `docs/plan/` | Plan snapshots |

## Conventions

- Central package versions in `Directory.Packages.props`; no `Version=` on `PackageReference`.
- Warnings are errors (`Directory.Build.props`). File-scoped namespaces.
- xUnit v3 on Microsoft.Testing.Platform (`dotnet test --solution …`). Every test project needs at
  least one test or the runner fails.
- One milestone per session. Write the contract and round-trip tests before the feature code for each
  engine call, and end with all tests green.
- Small commits with clear messages.
