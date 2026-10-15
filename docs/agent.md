# The agent (Phase 1b)

`src/CashGear.Backup.Agent` is the Windows service that owns state, secrets, schedules and the engine. The
desktop app (Phase 2) talks to it only through the gRPC API in
[`src/CashGear.Backup.Contracts/Protos/backup_agent.proto`](../src/CashGear.Backup.Contracts/Protos/backup_agent.proto)
over a local named pipe.

## Moving parts

| Piece | Where | What it does |
|---|---|---|
| Host | `AgentHost.cs` | Kestrel on the named pipe only (no TCP), gRPC, state.db, Quartz, engine, Serilog. `Program.cs` runs it as the `CashGearBackupAgent` service or as a console app |
| Lifecycle | `Hosting/AgentLifecycle.cs` | Start: migrate state.db, mark runs cut off by the last stop as failed, machine identity, engine, scheduler; then in the background connect destinations, schedule jobs, catch up missed runs, report `ready`. Stop: scheduler, runs, engine |
| state.db | `src/CashGear.Backup.Data` | EF Core + SQLite (WAL): destinations, jobs, run history, settings. Migrations in `Migrations/` |
| Secrets | `Security/DpapiSecretProtector.cs` | Repository passwords stored only as DPAPI blobs (machine scope + app entropy); decrypted just to hand to the engine |
| Who may call | `Security/CallerAuthorization.cs`, `Hosting/PipeAccess.cs` | Pipe ACL plus per-call access levels (below) |
| Runs | `Runs/RunCoordinator.cs` | Starts backups and restores, records every run, follows progress, stores the result |
| Schedules | `Scheduling/` | Daily (time + days) or every N hours; Quartz triggers rebuilt from state.db at each start |
| API | `Api/BackupAgentService.cs` | The gRPC service; `ErrorInterceptor` maps errors to statuses plus a `cashgear-error-kind` trailer |

## Security model

- **Pipe ACL:** SYSTEM, Administrators and the agent's own account have full control; interactive users may
  read and write data but not create pipe instances; network, anonymous and service logons are not let in.
  The first pipe instance is created with `FirstPipeInstance`, so the agent refuses to start if another
  process already holds the name.
- **Clients** open the pipe with read/write data rights only (`AgentChannel.ClientAccessRights`, not
  `GENERIC_WRITE`, which would also ask for the create-instance right) and impersonation level
  Identification: the agent can tell who they are but cannot act as them.
- **Access levels:** Read (status, lists, progress, browse) and Operate (run now, cancel) are open to anyone
  the pipe lets in. **Manage** (create or connect destinations, create, change or delete jobs, restore) needs
  an elevated administrator or SYSTEM. `Agent:AllowNonElevatedManage` lifts that for tests and development
  and must stay `false` in the shipped appsettings.json.
- **Decided 2026-10-15: Manage stays admin-only, and the desktop app asks for elevation (UAC) when it needs
  it.** The app runs as the user for everything Read and Operate. Setting up destinations or jobs and
  starting a restore go through an elevated process (the app relaunched elevated, or a small elevated
  helper) whose connection then carries an elevated token. `AgentStatus.caller_can_manage` tells the app
  whether its current connection may make Manage calls; a Manage call without it fails with
  `PermissionDenied`.

## Behaviour worth knowing

- **Every backup attempt is recorded.** An unplugged destination or a missing source folder gives a Failed
  run with the reason, without calling the engine. A restore the engine refuses (non-empty target) is an
  error to the caller and is not recorded.
- **A crash leaves no run "running":** at start, runs still marked Running become Failed ("The agent stopped
  during this run").
- **Missed schedules run once.** state.db is the only source of schedules (Quartz uses its in-memory store).
  At start, a job whose next time after its last run (or last change) has passed runs once with trigger
  CatchUp. While the agent runs, a time missed during sleep fires once on wake.
- **One job per folder per destination**, enforced in state.db: two jobs on one folder would share the
  engine's snapshot history and prune each other's snapshots. A job's folder and destination cannot change
  later; deleting a job keeps its backups and history, and a new job on the same folder sees them again.
- **Machine identity** (`cg-<16 hex>`) is generated once and stored; snapshots are recorded under it, so
  renaming the PC does not orphan them.
- Logs: `<data>\logs\agent-<date>.log` (14 days, 20 MB per file), plus the Event Log (source "CashGear
  Backup", warnings and up) when running as a service.

## Phase 3: checks, alerts, email, restore to the original folder

| Piece | Where | What it does |
|---|---|---|
| Checks | `Runs/RunCoordinator.StartVerifyAsync` | A run of kind Verify through `IBackupEngine.VerifyAsync` (structure of every snapshot plus a sampled read-back); one per job at a time; cannot be canceled |
| Housekeeping | `Notifications/Housekeeping.cs` | Every 5 minutes once ready: start checks that are due (a job with a restore point whose last check, or creation, is older than `verify_every_days`), then email alerts not emailed before |
| Alerts | `Notifications/AlertService.cs` | Worked out from state.db on each call, per enabled job: latest check failed, latest backup failed, latest backup skipped files, no restore point for `alert_after_days`. Ids are stable while the condition lasts, so each one is emailed once |
| Settings | `Notifications/SettingsStore.cs` | One JSON row in state.db's settings table; the email password inside it as a DPAPI blob; never returned by `GetSettings` |
| Email | `Notifications/EmailSender.cs` | MailKit; STARTTLS is required (not opportunistic) when chosen; no protocol log; credentials over unencrypted SMTP are refused |
| Restore to the original folder | `Runs/RunCoordinator.StartRestoreToOriginalAsync`, `Runs/KeepBothMerge.cs` | Engine restore into a hidden `.cashgear-restore-<run>` folder inside the job's folder, then a merge that never replaces anything; the job counts as busy meanwhile (no backup starts) |
| Search | `Runs/SnapshotSearch.cs` | Breadth-first walk of a snapshot through `BrowseAsync`, streamed; stops at the result limit or 20,000 folders |

Access levels: `SearchSnapshot`, `ListAlerts` and `GetSettings` are Read; `VerifyJobNow` is Operate; `UpdateSettings`
and `SendTestEmail` are Manage (`SendTestEmail` makes the LocalSystem agent connect to a server the caller names).

## Findings while building it

| # | Observed (.NET 10) | Consequence |
|---|---|---|
| 1 | `NamedPipeServerStream.RunAsClient` on a request thread: after it returns, the client's identification-level token turns up again in later async continuations of the same request, and every file or pipe access there fails with `ERROR_BAD_IMPERSONATION_LEVEL` (first seen as `Process.Start` failing to create its stdio pipes) | `CallerAuthorization` impersonates only on a throwaway thread with `ExecutionContext` flow suppressed, and only copies the token there |
| 2 | Kestrel's named-pipe transport defaults to `CurrentUserOnly = true`, which cannot be combined with a custom `PipeSecurity` | Set `CurrentUserOnly = false` explicitly and supply the ACL |
| 3 | EF Core's `DateTimeOffsetToBinaryConverter` encodes local wall-clock ticks, so ordering follows the wall clock, not the instant | `DateTimeOffsetToUtcTicksConverter`; the state.db test orders two runs whose wall clocks disagree with their instants |
| 4 | Opening a pipe with `GENERIC_WRITE` also requests `FILE_APPEND_DATA` = `FILE_CREATE_PIPE_INSTANCE` | Clients ask for `ReadWrite \| Synchronize` explicitly; the ACL never grants interactive users the create-instance right |
| 5 | Quartz.NET 4.0 shipped on 2026-09-08 with a new configuration model and had 11 releases in five weeks | Pinned the maintained 3.x line (3.22.6); revisit when 4.x settles |

## For review (CLAUDE.md rule 10)

1. ~~Manage needs elevation~~ **Decided:** admin-only, with UAC prompts in the desktop app (see "Security
   model"). Relaxing it would let a standard user make the LocalSystem agent read another user's files or
   write into protected folders.
2. **Browse is Read-level:** any interactive user can list file names inside any job's snapshots.
3. **DPAPI machine scope** (plan). Protection then rests on state.db's ACL (SYSTEM and Administrators,
   applied by the installer in Phase 4). `CurrentUser` scope under LocalSystem would bind blobs to the SYSTEM
   account instead; it breaks if the service account ever changes.
4. **Minimum password length 8** for new repositories (there is no recovery for a lost password; the Recovery
   Kit, Phase 3, is the backstop).
5. **Failed-run semantics:** a scheduled backup with its destination unplugged is a Failed run, not
   skipped or retried later. Phase 3's alerts report it (backup failed, then no backup for N days); "run when the
   NAS is reachable" (plan) still needs a retry policy.
6. **Deleting a job never deletes backups.** Removing a destination or deleting snapshots is not in the API
   yet.
7. **Restore to the original folder** (Phase 3) writes into the user's own folder as LocalSystem. It never
   replaces: missing items move in, identical files are left, a differing file is kept and the restored copy goes
   beside it as `name (restored yyyy-MM-dd HHmm).ext`; a file or link standing where a folder belongs is kept and
   the folder is restored beside it; links in the restored tree are moved as links, never followed. Items that
   cannot be moved stay in the staging folder, which is then unhidden and named in the run's message. A staging
   folder left by a crash is deleted at the next start (it only holds restored copies).
8. **Email password** (Phase 3): DPAPI machine scope like repository passwords, write-only over the API, kept
   out of logs; MailKit's protocol log is never enabled. Unencrypted SMTP is allowed only without a login.
9. **Alerts by email** go to whatever addresses an administrator enters; their text names jobs and folder paths.

## Not done yet

- Installer work (Phase 4): data folder ACL, Event Log source, service registration.
- ~~The client side of pipe-squatting protection~~ Done in Phase 2: `AgentServerVerifier` checks every connection
  (docs/desktop.md).
- Paging for very large folders in `BrowseSnapshot` (the client's default 4 MB message limit).
- Notifications and "no backup in N days" alerts (Phase 3); licence checks before runs (Phase 4).
