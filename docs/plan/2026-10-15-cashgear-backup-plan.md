> **Snapshot** of the live plan doc [Cash Gear Backup — Implementation Plan](https://claude.ai/artifact/TH8E7hrofjqwptoiV1wZKf), taken 2026-10-15 (doc revision 10). The live doc is the source of truth: if the two differ, the live doc wins. Kopia facts verified against v0.23.1 that refine this plan are in [spikes/KopiaSpike/FINDINGS.md](../../spikes/KopiaSpike/FINDINGS.md).

# Cash Gear Backup — Implementation Plan (Kopia Engine + C# Shell)

2026-10-15 · @Sadat

## Overview

Ship a Windows file-backup product in about 16 weeks by using Kopia (Go, Apache-2.0) as the backup engine and writing everything the customer sees in C#. Kopia already handles deduplication, encryption, compression, snapshots, retention and cloud storage. Our code adds the product: a Windows Service, a Blazor Hybrid UI, scheduling, notifications, restore wizards and licensing.

**v1 scope (file-level backup):**

- Back up folders and whole drives' files to local disk, NAS (SMB), SFTP and S3-compatible cloud (AWS S3, Backblaze B2, Wasabi).
- Incremental-forever with deduplication, AES-256 encryption, zstd compression.
- Open/locked files via Volume Shadow Copy.
- Schedules, retention (keep N daily/weekly/monthly), email and tray notifications.
- Browse versions and restore files or folders to the original or a new location.

**Out of scope for v1:** disk/partition imaging, bare-metal restore, bootable WinPE media, Outlook/SQL Server application-aware backup, macOS/Linux clients. Keep the engine behind an `IBackupEngine` interface so these can come later without rewriting the app.

## Architecture

Three processes: a desktop app the user sees, a Windows Service that does the work, and a bundled Kopia engine only the service can reach.

> _Diagram in the live doc: architecture · app, agent, Kopia engine, destinations_

The highlighted agent is the heart of your code; the shaded Kopia box is the bundled open-source engine.

- **Desktop app** can close at any time; backups keep running because all scheduling and engine control live in the agent.
- **Agent** owns state (`state.db`), secrets and the Kopia processes. It exposes one gRPC service to the UI: jobs, runs, live progress stream, snapshots, restore, settings.
- **Kopia server** runs one process per repository (destination), bound to localhost, started and stopped by the agent.
- **Typical backup run:** Quartz fires → agent checks destination reachable and licence → applies policy → `POST /api/v1/sources/upload` → polls progress and streams it to the UI → records result in `state.db` → sends notification.

## Tech stack

Everything except the engine is .NET 10, so you own and can review all product code.

| Layer | Choice | Why |
| --- | --- | --- |
| Backup engine | [Kopia](https://github.com/kopia/kopia) `kopia.exe`, pinned version, bundled | Dedup, encryption, compression, retention, VSS, many backends; Apache-2.0 |
| Background agent | .NET Worker Service + `Microsoft.Extensions.Hosting.WindowsServices` | Runs as LocalSystem, backs up with no user logged in |
| UI | Blazor Hybrid in WPF (`Microsoft.AspNetCore.Components.WebView.Wpf`) + MudBlazor or your CG controls | Reuses your Blazor skills; Windows-only, smaller than MAUI |
| Tray icon | `H.NotifyIcon.Wpf` | Status, "Back up now", open app |
| UI ↔ agent | gRPC over named pipes (`Grpc.AspNetCore` + `Grpc.Net.Client`) | Typed contract, streaming progress, no open TCP port |
| Agent ↔ Kopia | `HttpClient` to Kopia server on 127.0.0.1, TLS pinned, random credentials | Same API KopiaUI uses |
| Scheduling | Quartz.NET with SQLite job store | Misfire handling, calendars, survives restarts |
| Local state | SQLite + EF Core | Jobs, run history, settings, licence |
| Secrets | DPAPI `ProtectedData` (LocalMachine scope) | Repository passwords and cloud keys never stored in plain text |
| Logging | Serilog → rolling files + Windows Event Log | Support diagnostics |
| Notifications | MailKit (email), Windows toast via the tray app | Success/failure alerts |
| Installer | WiX Toolset MSI | Installs the Windows Service, firewall-free, enterprise-friendly |
| Updates | Own update check in the agent, signed MSI download | Updates both the service and `kopia.exe` together |
| Tests | xUnit, bUnit, Testcontainers (MinIO for S3) | Engine, UI components, cloud targets |

## Solution structure

One solution, eight projects; only `Engine.Kopia` knows Kopia exists.

```
CashGear.Backup.sln
├─ src/
│  ├─ CashGear.Backup.Core            // Domain: BackupJob, Destination, RetentionRule, RunResult, IBackupEngine
│  ├─ CashGear.Backup.Engine.Kopia    // IBackupEngine impl: process supervisor, REST client, DTOs, policy mapping
│  ├─ CashGear.Backup.Agent           // Windows Service: Quartz scheduler, gRPC server, notifications, updater
│  ├─ CashGear.Backup.Data            // EF Core + SQLite: jobs, runs, settings, licence; migrations
│  ├─ CashGear.Backup.Contracts       // .proto files + generated gRPC client/server types
│  ├─ CashGear.Backup.UI.Components   // Razor class library: pages, wizards, file browser, progress views
│  └─ CashGear.Backup.Desktop         // WPF host: BlazorWebView, tray icon, single-instance, deep links
├─ installer/
│  └─ CashGear.Backup.Installer       // WiX: service install, kopia.exe, ProgramData folders, ACLs
├─ tools/kopia/                       // pinned kopia.exe + SHA-256 + LICENSE + NOTICE
└─ tests/
   ├─ Core.Tests  ├─ Engine.Kopia.IntegrationTests  ├─ Agent.Tests  └─ UI.Components.Tests
```

**Runtime folders:**

- `C:\Program Files\CashGear Backup\` — binaries and `kopia.exe` (admin-only write).
- `C:\ProgramData\CashGear\Backup\` — `state.db`, per-repository Kopia config files, Kopia cache, logs. ACL: SYSTEM + Administrators only.

**Core contract** (keep it engine-agnostic):

```csharp
public interface IBackupEngine
{
    Task<RepositoryInfo> CreateRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct);
    Task<RepositoryInfo> ConnectRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct);
    Task ApplyJobPolicyAsync(BackupJob job, CancellationToken ct);           // retention, excludes, compression, VSS
    Task<RunHandle> StartBackupAsync(BackupJob job, CancellationToken ct);
    IAsyncEnumerable<RunProgress> WatchAsync(RunHandle run, CancellationToken ct);
    Task CancelAsync(RunHandle run, CancellationToken ct);
    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(BackupJob job, CancellationToken ct);
    Task<IReadOnlyList<SnapshotEntry>> BrowseAsync(SnapshotInfo snap, string path, CancellationToken ct);
    Task<RunHandle> RestoreAsync(RestoreRequest request, CancellationToken ct);
    Task<VerifyResult> VerifyAsync(BackupJob job, double sampleFraction, CancellationToken ct);
    Task RunMaintenanceAsync(RepositoryInfo repo, bool full, CancellationToken ct);
}
```

## Kopia integration

The agent supervises one `kopia server` process per repository and drives it over its local REST API; our scheduler decides when backups run.

**1. Bundling and version pinning**

- Ship one pinned `kopia.exe` in the MSI; verify its SHA-256 at agent start and refuse to run on mismatch.
- Upgrade Kopia only through your own release, after the integration test suite passes against the new version.
- Read the Kopia [compatibility notes](https://kopia.io/docs/advanced/compatibility/) before every bump: repository format changes are one-way.

**2. Process supervision (****`KopiaProcessHost`****)**

- Per repository, start: `kopia server start --config-file=<ProgramData>\repos\<id>\repository.config --address=127.0.0.1:<free port> --server-username=<random> --server-password=<random> --tls-cert-file=… --tls-key-file=…`, with the built-in web UI turned off.
- Generate the TLS cert and the credentials fresh on each agent start; pin the cert thumbprint in `HttpClient`. Nothing else on the machine can drive the engine.
- Use a Windows Job Object so `kopia.exe` dies if the agent crashes; health-check the API every 30 s and restart with back-off.
- Graceful stop: `POST /api/v1/shutdown`, then wait, then kill.

**3. API surface we use** (from Kopia's [`internal/server`](https://github.com/kopia/kopia/blob/master/internal/server/server.go) routes)

| Need | Endpoint |
| --- | --- |
| List backup sources | `GET /api/v1/sources` |
| Add a source | `POST /api/v1/sources` |
| Start a backup now | `POST /api/v1/sources/upload` |
| Cancel a running backup | `POST /api/v1/sources/cancel` |
| List snapshots | `GET /api/v1/snapshots` |
| Read/write policy | `GET` / `PUT /api/v1/policy` |
| Browse a snapshot folder | `GET /api/v1/objects/{objectID}` |
| Repo status, create, connect; restore; tasks and progress | `/api/v1/repo/*`, `/api/v1/restore`, `/api/v1/tasks/*` — confirm exact shapes in the pinned version |

This API lives in Kopia's `internal` package, so it is not a published, stable contract. Generate the C# DTOs from the pinned version's `serverapi` Go types, and keep a contract test per endpoint so a Kopia bump that breaks something fails CI, not a customer. Fallback for anything missing: run `kopia.exe … --json` and parse stdout.

**4. Mapping our jobs to Kopia policies**

Kopia's [`policy set`](https://kopia.io/docs/reference/command-line/common/policy-set/) covers the per-job settings; the agent writes the same fields through `PUT /api/v1/policy`.

| Our setting | Kopia policy field |
| --- | --- |
| Schedule | `--manual` (Kopia never schedules; Quartz triggers `sources/upload`) |
| Open/locked files | `--enable-volume-shadow-copy=when-available` (agent runs as LocalSystem) |
| Keep last N / daily / weekly / monthly / yearly | `--keep-latest`, `--keep-daily`, `--keep-weekly`, `--keep-monthly`, `--keep-annual` |
| Exclusions | `--add-ignore` (plus our presets: temp, caches, `node_modules`, `bin/obj`) |
| Compression | `--compression` (zstd family), `--add-never-compress` for media and archives |
| Skip unreadable files | `--ignore-file-errors` (surface the skipped list in the run report) |

Why our scheduler instead of Kopia's: we need "run when the NAS is reachable", "skip on metered network", wake-from-sleep, and one notification pipeline. Kopia's `--snapshot-time` scheduling stays unused.

**5. Repository password and recovery**

- The user's encryption password is stored with DPAPI (machine scope) and passed to Kopia only via the `KOPIA_PASSWORD` environment variable of the child process.
- At repository creation, force the user to save a **Recovery Kit** PDF (repository location + password + restore steps). Without the password, data is unrecoverable — say so plainly in the UI.

**6. Maintenance**

- Kopia needs periodic [maintenance](https://kopia.io/docs/advanced/maintenance/) to reclaim space after retention deletes snapshots. Schedule quick maintenance daily and full maintenance weekly, outside backup windows, as their own Quartz jobs.

## Roadmap

Six phases take the product from a one-week spike to a v1 release in week 16; no phase starts until the previous gate passes.

> _Diagram in the live doc: roadmap · 6 phases to v1, each with an exit gate_

Each gate is a test you can run, so a coding agent can work a phase end to end and prove it is done.

**Phase 0 · Spike (week 1)**

- [ ] Console app starts `kopia server`, creates a filesystem repository, backs up a folder, lists snapshots, restores to a temp folder
- [ ] Byte-compare test of the restore passes
- [ ] Note every API shape that differs from expectations

**Phase 1 · Engine and agent (weeks 2–4)**

- [ ] `KopiaProcessHost`: start, health check, restart, shutdown, Job Object
- [ ] Typed REST client + DTOs; contract tests per endpoint
- [ ] `IBackupEngine` implemented for create/connect, policy, backup, progress, cancel, list
- [ ] Windows Service host, SQLite schema + migrations, Quartz jobs, gRPC over named pipe

**Phase 2 · Desktop app (weeks 5–8)**

- [ ] WPF + BlazorWebView shell, tray icon, single instance
- [ ] Job wizard: what (folders, presets), where (destination), when (schedule), how long (retention)
- [ ] Destinations: local/USB, SMB with credentials, SFTP, S3-compatible
- [ ] Dashboard: last good backup per job, live progress, history

**Phase 3 · Restore and safety (weeks 9–11)**

- [ ] Snapshot timeline + file browser with search
- [ ] Restore wizard: original or new location, keep-both default
- [ ] Verify jobs, maintenance jobs, "no backup in N days" alert, email + toast
- [ ] Recovery Kit PDF at repository creation

**Phase 4 · Ship-ready (weeks 12–14)**

- [ ] WiX MSI with service install, ACLs, silent switches
- [ ] Code signing, update manifest + updater, licence activation and grace period
- [ ] Support bundle (logs + config without secrets), open-source notices screen

**Phase 5 · Beta and hardening (weeks 15–16)**

- [ ] Chaos and scale suites green
- [ ] 10–20 beta machines for two weeks; fix list closed
- [ ] Help docs: first backup, restore, moving to a new PC

## Data safety, security and testing

A backup product is judged on one thing: the restore works. Every release must prove that automatically.

**Data safety rules**

- Never mark a run "Succeeded" unless Kopia returned a snapshot ID; partial runs show "Completed with warnings" plus the skipped-file list.
- Weekly automatic verification: Kopia [consistency checks](https://kopia.io/docs/advanced/consistency/) with a sampled file read (start at 5%, user-adjustable).
- Alert if no successful backup in N days (default 3), even when no run failed (e.g. the PC was off).
- Ransomware: offer S3 Object Lock / B2 immutable buckets per Kopia's [ransomware protection](https://kopia.io/docs/advanced/ransomware-protection/) guide; warn when the destination is a mapped drive the user can write to.
- Restore never overwrites by default: "keep both" or "restore to new folder" first.

**Security**

- Agent runs as LocalSystem; the UI runs as the user. The named pipe ACL allows Administrators and the interactive user; destructive actions (delete repository, change password) require elevation.
- Kopia API: localhost only, random credentials per start, pinned TLS.
- Secrets: DPAPI at rest, never logged; Serilog destructuring policy masks any `Password`, `Secret`, `Key` property.
- Sign every binary (`kopia.exe` re-verified by hash, our assemblies and MSI Authenticode-signed).

**Testing**

| Layer | What | How |
| --- | --- | --- |
| Unit | Retention mapping, schedule rules, job validation | xUnit, no Kopia |
| Contract | Each Kopia endpoint request/response shape | Real pinned `kopia.exe`, filesystem repo in temp dir |
| Integration | Backup → change files → incremental → restore → byte-compare (SHA-256 of every file) | Local disk, SMB share, MinIO via Testcontainers, SFTP container |
| Locked files | Back up an open SQLite/Outlook-style locked file | VSS on, run elevated on CI agent |
| Chaos | Kill agent mid-backup, pull network, fill destination disk | Next run must succeed; repository must verify clean |
| Scale | 1M small files; one 200 GB file | Track duration and RAM per release |
| UI | Wizards, file browser, progress | bUnit + a manual test script per release |

The round-trip restore test (back up, restore, compare every byte) is the release gate: it must pass on every supported destination before shipping.

## Packaging, updates and licensing

Ship one signed MSI that installs the service, the desktop app and the pinned engine together, so versions never drift.

**Installer (WiX)**

- Installs to Program Files, creates ProgramData folders with SYSTEM/Administrators ACLs, registers the `CashGearBackupAgent` service (automatic, delayed start, restart on failure).
- Per-machine install only (the service needs admin). Silent install switches for IT deployments.
- Uninstall stops the service but **never deletes backup repositories**; ask before removing local settings.

**Updates**

- Agent checks a signed JSON manifest daily, downloads the MSI, verifies the Authenticode signature, installs only when no backup is running.
- Engine upgrades ride inside app updates; never let the user swap `kopia.exe` on their own.

**Licensing and editions**

- Free: 1 job, local/NAS destinations. Pro: unlimited jobs, cloud destinations, email alerts. Business: silent install, central reporting later.
- Licence keys validated by your own licence server (could share Cash Gear's accounts); 30-day offline grace so backups never stop because the licence server is down.
- A lapsed licence blocks new backups after the grace period but **never blocks restore**.

**Kopia licence compliance (Apache-2.0)**

- Commercial use and closed-source products are allowed.
- Ship Kopia's `LICENSE` and any `NOTICE` file, list it in an "Open-source components" screen, and state if you modified it.
- Don't use the Kopia name or logo as your product branding.

## Risks and coding-agent tips

The biggest risk is Kopia's API changing under you; contract tests and version pinning contain it.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Kopia internal API changes in a new release | Backups or restores break after an upgrade | Pin version; contract tests per endpoint; CLI `--json` fallback |
| User loses repository password | Backups unrecoverable | Mandatory Recovery Kit; optional escrow of an encrypted copy (Business) |
| Silent failures (PC off, destination offline) | Customer discovers no backups when disaster hits | "No backup in N days" alert; dashboard shows last good backup per job |
| Kopia repository corruption | Lost history | Weekly verify; offer a second destination (3-2-1 rule) in the job wizard |
| VSS failures on some machines | Locked files skipped | `when-available` mode, list skipped files, link to a fix guide |
| Kopia project slows or stops | No upstream fixes | `IBackupEngine` boundary; Apache-2.0 lets you fork; restic is a second engine candidate |
| Competing with EaseUS on features | Customers expect disk imaging | Market v1 as file backup; imaging is a Phase 6 decision |

**Driving coding agents on this project**

1. Put this plan plus an `AGENTS.md` / `CLAUDE.md` in the repo root: architecture rules (only `Engine.Kopia` touches Kopia, no secrets in logs, async everywhere).
2. Give the agent one milestone at a time, each ending with tests green, not one giant prompt.
3. Have the agent write the contract and round-trip tests **before** the feature code for each engine call.
4. Keep a real `kopia.exe` in `tools/` so the agent can run integration tests locally instead of guessing API shapes.
5. Review by hand anything touching passwords, deletion, retention and restore overwrite; that's where agent mistakes cost customer data.

## Sources

- [Kopia ](https://kopia.io/docs/reference/command-line/common/policy-set/)[`policy set`](https://kopia.io/docs/reference/command-line/common/policy-set/)[ reference](https://kopia.io/docs/reference/command-line/common/policy-set/) — VSS, retention, manual scheduling, compression flags
- [Kopia server routes (](https://github.com/kopia/kopia/blob/master/internal/server/server.go)[`internal/server/server.go`](https://github.com/kopia/kopia/blob/master/internal/server/server.go)[)](https://github.com/kopia/kopia/blob/master/internal/server/server.go) — REST endpoints used by KopiaUI
- [Kopia common commands](https://kopia.io/docs/reference/command-line/common/) — `server start`, `server snapshot`, `server shutdown`
- [Kopia repository server](https://kopia.io/docs/repository-server/) — TLS and user setup
- [Kopia on GitHub](https://github.com/kopia/kopia) — source and Apache-2.0 licence
