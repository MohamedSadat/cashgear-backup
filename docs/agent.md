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
   Kit is Phase 3).
5. **Failed-run semantics:** a scheduled backup with its destination unplugged is a Failed run, not
   skipped or retried later; "run when the NAS is reachable" (plan) needs a retry policy in Phase 2 or 3.
6. **Deleting a job never deletes backups.** Removing a destination or deleting snapshots is not in the API
   yet.

## Not done yet

- Installer work (Phase 4): data folder ACL, Event Log source, service registration.
- ~~The client side of pipe-squatting protection~~ Done in Phase 2: `AgentServerVerifier` checks every connection
  (docs/desktop.md).
- Paging for very large folders in `BrowseSnapshot` (the client's default 4 MB message limit).
- Notifications and "no backup in N days" alerts (Phase 3); licence checks before runs (Phase 4).
