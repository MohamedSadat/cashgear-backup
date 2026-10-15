# CashGear Backup — guide for contributors and coding agents

Windows file-backup product. Kopia (Go, Apache-2.0) is the bundled backup engine; everything the
customer sees is .NET 10 C#.

- **Plan (source of truth):** the live doc
  [Cash Gear Backup — Implementation Plan](https://claude.ai/artifact/TH8E7hrofjqwptoiV1wZKf).
  Snapshot: [docs/plan/2026-10-15-cashgear-backup-plan.md](docs/plan/2026-10-15-cashgear-backup-plan.md).
- **Verified Kopia behaviour:** [spikes/KopiaSpike/FINDINGS.md](spikes/KopiaSpike/FINDINGS.md). Where it
  disagrees with the plan, FINDINGS wins (it was observed against the pinned binary).
- **Agent design, security model and its findings:** [docs/agent.md](docs/agent.md).
- **Status:** Foundation, Phase 0 and Phase 1 done: the Kopia engine behind `IBackupEngine` (1a) and the
  agent with state.db, schedules and the gRPC pipe API (1b). Next: Phase 2, the desktop app, against
  `backup_agent.proto`. Open decisions are at the end of FINDINGS and of docs/agent.md.

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
`IBackupEngine` (in Core); `KopiaBackupEngine` implements it, and the agent gets it through
`IBackupEngineFactory` (`services.AddBundledBackupEngine()`).

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
11. **The agent runs as LocalSystem; the pipe API is its attack surface.** Calls that make it read or
    write a folder the caller names (destinations, jobs, restore) are Manage-level and need an elevated
    administrator; `Agent:AllowNonElevatedManage` is for tests only. Never impersonate the pipe client
    on a request thread (docs/agent.md, finding 1), and never log gRPC request messages (they can carry
    passwords).

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1   # once per clone
dotnet build CashGear.Backup.slnx
dotnet test --solution CashGear.Backup.slnx
dotnet test --solution spikes\KopiaSpike.slnx          # Phase 0 gate: real kopia round trip
dotnet run --project spikes\KopiaSpike -- --keep-workspace
dotnet tool restore                                      # dotnet-ef, for state.db migrations
dotnet tool run dotnet-ef migrations add <Name> -p src\CashGear.Backup.Data -s src\CashGear.Backup.Data -o Migrations
```

Run the agent as a console app (it does not need to be a service) with its data outside ProgramData:

```powershell
$env:Agent__DataDirectory = "$env:TEMP\cashgear-dev"; $env:Agent__AllowNonElevatedManage = "true"
dotnet run --project src\CashGear.Backup.Agent
```

Tests that need Kopia find it via `CASHGEAR_KOPIA_EXE` or `tools/kopia/kopia.exe` and fail with the
fetch command if it is missing or does not match the pin (a new git worktree needs the fetch too). The
engine and agent tests take about a minute each. To debug them, set `CASHGEAR_KEEP_TEST_WORKSPACE=1` to keep
`%TEMP%\cashgear-tests` and `%TEMP%\cashgear-agent-tests` (the agent's log is in `data\logs` there), and
`CASHGEAR_TEST_LOG_DIR` for one engine log per engine test, passing tests included.

## Repo map

| Path | What |
|---|---|
| `src/CashGear.Backup.Core` | Domain types and `IBackupEngine` (engine-agnostic) |
| `src/CashGear.Backup.Engine.Kopia` | The only Kopia-aware code: `KopiaBackupEngine`; `Api/` REST client and DTOs, `Hosting/` process host, pin, Job Object, `Policy/` job-to-policy mapping, `Runs/` run tracking and the rule-5 classifier |
| `src/CashGear.Backup.Agent` | Windows Service: `AgentHost` (pipe-only Kestrel, gRPC), `Api/`, `Runs/`, `Scheduling/` (Quartz), `Security/` (DPAPI, caller checks), `Hosting/` (lifecycle, pipe ACL). Later: notifications, updater |
| `src/CashGear.Backup.Data` | EF Core + SQLite state.db: destinations, jobs, run history, settings; `Migrations/` |
| `src/CashGear.Backup.Contracts` | `Protos/backup_agent.proto` (the desktop app's API) and `AgentChannel` (named-pipe gRPC channel) |
| `src/CashGear.Backup.UI.Components` | Razor class library: pages, wizards, file browser |
| `src/CashGear.Backup.Desktop` | WPF host: BlazorWebView, tray icon |
| `installer/` | WiX MSI (Phase 4; placeholder for now) |
| `tools/kopia/` | Kopia pin, fetch script, LICENSE (exe is gitignored) |
| `tests/` | xUnit v3 test projects; `tests/Shared` holds helpers linked into several projects (pinned binary, tree fixture and byte-compare manifest) |
| `tests/CashGear.Backup.Agent.Tests` | state.db, DPAPI, schedules, pipe ACL, and the whole agent hosted in-process on a private pipe, driven through `AgentChannel` against the pinned kopia.exe |
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
