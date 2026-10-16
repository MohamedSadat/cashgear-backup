# Chaos and scale suites (Phase 5)

`tests/CashGear.Backup.Hardening.Tests` holds the plan's Chaos and Scale rows (Testing table):

| Row | What | Gate |
|---|---|---|
| Chaos | Kill agent mid-backup, pull network, fill destination disk | Next run must succeed; repository must verify clean |
| Scale | 1M small files; one 200 GB file | Track duration and RAM per release |

Every test drives the real agent **as its own process** (the console form of the service, started from its build
output with its data in a test workspace and the pinned `kopia.exe`), over its named pipe, the way the desktop app
does. That is what lets a test kill it. The agent runs inside a kill-on-close Job Object owned by the test, which also
lists the agent's process tree (the kopia server, kopia CLI checks) and gives its peak memory.

The tests are xUnit **explicit** tests: `dotnet test --solution CashGear.Backup.slnx` builds them and reports them as
skipped ("Not run (due to explicit test filtering)"), and runs only the project's quick checks of its own tools.

## Running them

```powershell
# Chaos suite (about 45 minutes with the defaults; 4 to 6 minutes per test)
dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-trait "Category=Chaos"

# Scale suite, small variant (a few minutes): 20,000 files and a 1 GB file
dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-trait "Category=Scale"

# Scale suite, full run: on one hard disk about 3 hours without restores (CASHGEAR_SCALE_RESTORE=0, ~420 GB free)
# and 10+ hours with them (~630 GB free); see the results below
$env:CASHGEAR_SCALE_FILES = "1000000"; $env:CASHGEAR_SCALE_BIG_FILE_GB = "200"
$env:CASHGEAR_SCALE_DIR = "D:\cashgear-scale"; $env:CASHGEAR_SCALE_LABEL = "1.0.0 full run"
dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-trait "Category=Scale"

# One test
dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-method "*Agent_killed_mid_backup*"

# Only write the full-size sources ahead of a measured run (about 45 minutes on a hard disk)
dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-method "*Generate_sources"
```

The local-disk-full test attaches a VHDX, so it runs only from an elevated prompt (it is skipped otherwise).

Add `--output Detailed` to see each test's log (fault timing, run results, the independent check's report) in the
console. The tests run one at a time (the assembly turns off parallel runs). Starting the test exe directly uses
xUnit's own command line instead: `CashGear.Backup.Hardening.Tests.exe -explicit only -method "*One_big_file"`.

| Variable | Default | What |
|---|---|---|
| `CASHGEAR_CHAOS_SOURCE_MB` | 1024 | Data each chaos test backs up when its fault hits. A test fails, saying so, if the backup ended before the fault: raise it on a fast machine |
| `CASHGEAR_CHAOS_FAULT_WAIT_MIN` | 2.5 | How long after the fault a run that lost its destination may take to end by itself (the engine ends it after 90 s of failed writes, finding 3). A run still going then is cancelled and the test fails |
| `CASHGEAR_SCALE_FILES` | 20000 | Files in the many-files scenario (0 to 8 KiB each, random) |
| `CASHGEAR_SCALE_BIG_FILE_GB` | 1 | Size of the single file in the big-file scenario, in GB (10^9 bytes) |
| `CASHGEAR_SCALE_DIR` | `%TEMP%\cashgear-scale` | Sources (kept between runs and reused while the size matches), repositories, restores, and `progress.log` to follow a long run |
| `CASHGEAR_SCALE_RESTORE` | on | `0` skips the restore and byte-compare (it needs the source's size again in free space) |
| `CASHGEAR_SCALE_RESULTS` | `artifacts\scale\results.jsonl` | One JSON line per measured phase is appended here |
| `CASHGEAR_SCALE_LABEL` | empty | Stored with each result, for example the release |
| `CASHGEAR_KEEP_TEST_WORKSPACE` | unset | Keep each test's folder (`%TEMP%\cashgear-hardening\...`): the agent's log in `data\logs`, Kopia's in `data\engine\repos\<id>\logs` |

They need `tools\kopia\kopia.exe` and the SFTP/S3 test servers (`tools\test-servers`), like the other integration tests.

## Chaos suite

Every chaos test sets up a job with one good restore point (the shared `TreeFixture`), adds `CASHGEAR_CHAOS_SOURCE_MB`
of random data (it neither compresses nor deduplicates), starts a backup and strikes once a quarter of the new data is
through. Then `Chaos/Recovery.cs` asserts the same thing for every fault:

1. **The faulted run is not Succeeded** (CLAUDE.md rule 5) and records no snapshot. A run that rode out a short fault
   may say Succeeded, but then its snapshot must restore byte for byte.
2. **The next run succeeds**, with no failed items.
3. **The repository verifies clean**, twice: the agent's own check with every file read back (`VerifyJobNow` at
   100 %), and an independent check with the pinned kopia CLI on a copy of the agent's `repository.config` with an
   empty cache: `snapshot verify --verify-files-percent=100` over every snapshot of every source, then `content verify
   --full` (every content against its pack, every pack downloaded).
4. **Restores**: the new restore point and the one taken before the fault both restore into new folders and
   byte-compare with their sources (`TreeManifest`).
5. Every Succeeded backup in the job's history points at a complete snapshot with nothing failed.

| Test | Fault |
|---|---|
| `AgentCrashTests.Agent_killed_mid_backup_takes_kopia_down_and_the_next_run_succeeds` | `TerminateProcess` on the agent only (a crash or power cut of the service). The kopia server must exit with it (the engine's kill-on-close Job Object), and the restarted agent must record the run as Failed ("The agent stopped during this run") |
| `AgentCrashTests.Kopia_server_killed_mid_backup_fails_the_run_and_the_next_run_succeeds` | The kopia server process is killed while the agent lives. The run must be Failed with "The backup engine stopped during the run (its process ended unexpectedly)", and the engine must start a new server within 20 s (it does so when the process exits, not at the next health check) |
| `NetworkDropTests.Network_down_mid_backup_never_succeeds_and_the_next_run_does` (SFTP, S3) | A TCP proxy between the agent and the test server resets every connection and every new one. The network stays down; the run must end by itself within `CASHGEAR_CHAOS_FAULT_WAIT_MIN` (2.5) minutes as Failed, "Lost the destination: it could not be reached ...". Then the network comes back |
| `NetworkDropTests.Network_back_after_an_outage_mid_backup_never_leaves_a_bad_restore_point` (SFTP, S3; 5 s and 60 s) | The network is down for 5 s (inside Kopia's retries) or 60 s (longer than them) and comes back while the run goes on |
| `DestinationFullTests.A_full_sftp_destination_fails_the_run_and_once_space_is_freed_the_next_run_succeeds` | SFTPGo in serve mode with a per-user quota: room for the first restore point and half the new data. The run must end by itself as Failed, "Lost the destination: it is full ...". Then the server restarts without a quota (space freed) |
| `DestinationFullTests.A_full_local_disk_fails_the_run_and_once_space_is_freed_the_next_run_succeeds` | A small VHDX volume mounted into a folder, mostly taken by another file; then that file is deleted. **Needs an elevated prompt** (attaching a VHDX); skipped otherwise |

## Scale suite

`ScaleTests.Many_small_files` (`CASHGEAR_SCALE_FILES` files of 0 to 8 KiB in folders of 1,000) and
`ScaleTests.One_big_file` (one file of `CASHGEAR_SCALE_BIG_FILE_GB`) each back up a generated source of random data to
a local folder through a fresh agent, and record four phases:

| Phase | What | Asserted |
|---|---|---|
| `first-backup` | The first, full backup | Succeeded, every file and byte counted |
| `unchanged-backup` | The same folder again, nothing changed | Succeeded |
| `check` | The agent's check at its default setting (5 % of files read back) | Succeeded |
| `restore` | Restore of the first snapshot into a new folder, then a byte-compare (SHA-256 of every file; the big file against the hash taken when it was written) | Succeeded, identical |

Each phase appends one JSON line to `CASHGEAR_SCALE_RESULTS`: commit, agent and Kopia versions, machine, source size,
duration, files and bytes, throughput, the repository's size, and peak memory: working set and private bytes of the
agent and of kopia (all kopia processes summed, since a check runs a CLI beside the server), sampled every 250 ms over
the phase, plus the Job Object's peak commit of the whole tree since the agent started. Compare these lines release by
release; the table below is the record of full runs.

### Results

Machine: Intel Xeon E5-2680 v3 (24 logical CPUs), 32 GB RAM, Windows 10 Pro 22H2, Windows Defender real-time
protection on. Agent 1.0.0 (Debug build of this branch), Kopia 0.23.1. Peak memory is working set / private bytes.

**Full run, 2026-10-16**, on D: (a WD VelociRaptor 10,000 rpm hard disk, NTFS) holding source, repository and restore
alike; 1,000,000 files of 0 to 8 KiB (3.8 GiB) and one file of 200 GB (186.3 GiB):

| Scenario | Phase | State | Files | Data | Time | Throughput | Agent peak | Kopia peak | Repository |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| 1M files | first-backup | Succeeded | 1,000,000 | 3.81 GiB | 14 min 19 s | 1,164 files/s | 159 / 76 MiB | 920 / 963 MiB | 3.95 GiB |
| 1M files | unchanged-backup | Succeeded | 1,000,000 | 3.81 GiB | 4.5 s | 222,000 files/s | 164 / 80 MiB | 803 / 811 MiB | 3.95 GiB |
| 1M files | check (5 %) | Succeeded | 50,208 read back | 0.19 GiB | 4 min 15 s | 197 files/s | 165 / 80 MiB | 1,334 / 1,327 MiB | 3.95 GiB |
| 1M files | restore | Succeeded | 1,000,000 | 3.81 GiB | 5 h 50 min | 48 files/s | not recorded | not recorded | |
| 200 GB file | first-backup | Succeeded | 1 | 186.26 GiB | 1 h 44 min | 30.5 MiB/s | 160 / 77 MiB | 1,059 / 1,083 MiB | 186.29 GiB |
| 200 GB file | unchanged-backup | Succeeded | 1 | 186.26 GiB | 0.5 s | | 136 / 52 MiB | 846 / 838 MiB | 186.29 GiB |
| 200 GB file | check (5 %) | Succeeded | 0 read back | 0 | 3.4 s | | 138 / 52 MiB | 982 / 992 MiB | 186.29 GiB |

Both scenarios were disk-bound, not CPU-bound. The 200 GB backup had the one disk reading the source and writing the
repository at about 31 and 25 MiB/s while kopia used about half a core; it ran without a restore
(`CASHGEAR_SCALE_RESTORE=0`) to keep the run to hours. During the 1M-file restore the disk was 100 % busy at about 450
I/O per second, roughly nine per small file. That restore's byte-compare was **stopped after about 3 hours**: Windows
Defender scanned each newly restored file as the test opened it (about 120 files/s), and the suite now hashes in
parallel. So for this run the 1M-file restore rests on the agent's result (Succeeded, 1,000,000 files, no errors), not
on a byte-compare, and that phase's JSON line (with its memory) was never written. The small variant below
byte-compares every file through the same code path.

**Small variant** (the defaults: 20,000 files and a 1 GB file), on C: (SATA SSD), same machine:

| Scenario | Phase | State | Files | Data | Time | Agent peak | Kopia peak |
|---|---|---|---:|---:|---:|---:|---:|
| 20k files | first-backup | Succeeded | 20,000 | 78 MiB | 1.4 s | 123 / 46 MiB | 287 / 407 MiB |
| 20k files | unchanged-backup | Succeeded | 20,000 | 78 MiB | 0.3 s | 124 / 47 MiB | 290 / 467 MiB |
| 20k files | check (5 %) | Succeeded | 984 read back | 4 MiB | 2.7 s | 133 / 52 MiB | 468 / 714 MiB |
| 20k files | restore + byte-compare | Succeeded, identical | 20,000 | 78 MiB | 65.6 s | 138 / 54 MiB | 408 / 573 MiB |
| 1 GB file | first-backup | Succeeded | 1 | 0.93 GiB | 13.8 s | 131 / 50 MiB | 222 / 270 MiB |
| 1 GB file | unchanged-backup | Succeeded | 1 | 0.93 GiB | 0.3 s | 131 / 50 MiB | 224 / 274 MiB |
| 1 GB file | check (5 %) | Succeeded | 0 read back | 0 | 1.1 s | 132 / 51 MiB | 319 / 362 MiB |
| 1 GB file | restore + byte-compare | Succeeded, identical | 1 | 0.93 GiB | 18.9 s | 138 / 54 MiB | 397 / 435 MiB |

What the numbers say so far:

- **Memory is flat in the agent** (about 160 MiB working set, 80 MiB private, for a million files and for 200 GB) and
  stays around 1 GiB in kopia: 0.9 GiB to back up a million files, 1.1 GiB for the 200 GB file, 1.3 GiB while a
  check's CLI runs beside the server. Nothing approached the machine's 32 GB.
- **Throughput was the disk's**: 1,164 small files/s and 30 MiB/s for one big file with source and repository on one
  hard disk. A separate destination disk (the usual USB or NAS case) should do better; measure it there.
- **Unchanged backups are cheap** even at a million files (4.5 s): Kopia compares sizes and times per folder.
- **Restoring many small files to a hard disk is slow** (48 files/s, about six hours for a million). The restore
  completed, but a user restoring a whole large folder tree to a hard disk should be told to expect hours; worth a
  note in the help docs (Phase 5) and a look at Kopia's restore parallelism.
- With one file, the 5 % check reads it back or not (each file is drawn with 5 % chance), so its time varies by run.

## What the chaos suite found (2026-10-16, Kopia 0.23.1)

Observed with the defaults (1 GB of new data per test) on the machine in the scale results below. Every gate held:
no faulted run was ever recorded as Succeeded, every next run succeeded, and every repository verified clean (agent
check at 100 % and the independent kopia check), with both restore points byte-identical. What the runs showed on
the way:

| # | Observed | Consequence |
|---|---|---|
| 1 | **Agent killed**: the kopia server exits with it (the engine's Job Object), the restarted agent records the run Failed ("The agent stopped during this run"), and the next run succeeds in 10 to 30 s, reusing what the killed run had uploaded | Works as designed |
| 2 | **Kopia server killed** while the agent runs: the run is Failed, but its message is "Following the run failed: A task was canceled." (or "The operation was canceled."), and the agent logs it as an error with a stack trace. When the health check (every 30 s) restarts the server, `KopiaProcessHost.RestartAsync` disposes the old `HttpClient` while `MonitorBackupAsync` is polling it; that cancellation is not a `TimeoutException`, so `IsKopiaFailure` does not match and the generic catch reports it. Until the health check notices, the dead server stays current, so a run started in that window fails too | Rule 5 holds. **Fixed** (below): the run is Failed with "The backup engine stopped during the run (its process ended unexpectedly) ...", logged as a warning without a stack, and the server is restarted as soon as its process exits |
| 3 | **Destination lost mid-run** (network down, or the NAS full): Kopia does not fail the snapshot. It retries each pack write 10 times (about 11 s), records that file as failed, and goes on with the next file. 6.5 minutes after the network to an SFTP server went down, a 1 GB run had failed 7 of the 24 remaining files (546 retried writes) and was still running; the full NAS behaved the same. **Cancelling does not help much while the destination is gone**: the cancel took 5 s and 100 s (full NAS), 6 min (SFTP down) and 14 min (S3 down). In the S3 case the kopia server stopped answering its health check meanwhile, the engine restarted it, the new server could not open the repository, and the run was finally recorded Failed with "The backup engine ... is restarting; try again later." A run of a large folder to an unplugged NAS would show "running" for hours, and block the job's next runs meanwhile | Rule 5 held every time: the run ended Failed (Kopia's task fails with the storage error, for example `denying write due to space limit`). **Fixed** (below): the engine ends the run itself as Failed, "Lost the destination: ...", once Kopia's writes have failed for 90 s, and the tests now require that |
| 4 | **Network back while the run goes on**: after 5 s, Kopia's retries ride it out and the run Succeeds with a whole snapshot (restored byte for byte by the test). After 60 s, the run ends CompletedWithWarnings with 19 (SFTP) or 22 (S3) files "could not be backed up": a restore point with warnings, as rule 5 intends. The next run picks those files up and Succeeds | Works as designed |
| 5 | The agent's check (`kopia snapshot verify`) fails on a single connection reset while listing blobs (`unable to list blobs: ... forcibly closed by the remote host`); reading blobs is retried 10 times, listing is not. Seen through a bug in the test's proxy (since fixed), not on a healthy network | A flaky network could raise a false "check failed" alert; low priority |
| 6 | **Full NAS, then space freed**: once in seven runs, the agent's check after recovery could not read one pack from SFTPGo (`permission denied` when opening `p/f84/...f`, after 10 retries), although the run after recovery Succeeded and the other runs verified clean | Not explained yet; see "Open" below |

## How the engine ends a run that lost its destination (findings 2 and 3)

**Destination lost (finding 3).** Kopia never fails a snapshot because its destination went away, and neither its
`Errors` counter (it lags by whole files) nor `Uploaded Bytes` (it rises during the outage) shows it. Its task log
does: every write it retries and every write it gives up on (FINDINGS.md, Phase 5). While a backup runs, the engine
reads that log every 2 s (`Runs/DestinationWatch`) and ends the run when

1. Kopia has **given up at least one write**: some files have already failed, so the run can no longer record a
   complete restore point (rule 5); retries that Kopia wins never count, and
2. its storage errors have gone on for **90 s** (`KopiaEngineOptions.DestinationLossWindow`) with no pause of
   **30 s** (`DestinationQuietPeriod`); a pause that long means writes go through again and the count starts afresh.
   During the observed outages a storage error came at least every 8.5 s.

The run is then recorded **Failed** (Canceled if the user had already asked to cancel) with no restore point and one
of three messages, from the most telling words seen during the outage: "Lost the destination: it is full ...", "...
it refused every write ... (access denied) ...", or "... it could not be reached for 90 s ...". The engine cancels
Kopia's upload and keeps the job's folder reserved until Kopia's task has really ended (it re-sends the cancel every
second), so a new run never starts on top of it: a new run waits up to 30 s (`StoppingRunWait`), then is refused,
"The last backup of ... lost its destination and Kopia is still stopping it". Kopia's last storage error goes to the
agent's log as a warning.

Before choosing the rule the session watched what Kopia shows (FINDINGS.md, Phase 5): first give-up 9 s (SFTP), 10 s
(full SFTPGo), 49 s (S3) and 3 s (a folder that refuses writes) after the fault. A TCP reachability probe was
considered and left out: it cannot see a full NAS, and Kopia's own errors already say "space limit", "Access is
denied" or "unable to dial". A 60 s outage stays below the window, so finding 4 is unchanged.

**Kopia server killed (finding 2).** `KopiaProcessHost.IsCurrent(generation)` is false as soon as a server is being
replaced (before its client is disposed), and a monitor whose poll fails on a replaced server records "The backup
engine stopped during the run (its process ended unexpectedly); the run was lost ..." (or "(it stopped answering)").
The health loop waits on the process's exit as well as on its 30 s timer, so a dead server is replaced at once.

Both change how runs end: **hand review (CLAUDE.md rule 10)** `Runs/DestinationWatch.cs`, the monitor and
`DrainAsync` in `KopiaBackupEngine.Runs.cs`, and `HealthLoopAsync` in `Hosting/KopiaProcessHost.cs`.

**Chaos suite after the fix** (2026-10-16, same machine and defaults as above): 9 passed, 1 skipped (local disk
full, needs elevation), 44 minutes. Every faulted run ended by itself and every repository verified clean (agent
check at 100 % and the independent kopia check), with both restore points byte-identical.

| Test | Faulted run | Ended | Next run |
|---|---|---|---|
| Network down, SFTP | Failed, "Lost the destination: it could not be reached for 92 s ..." | 93 s after the cut | Succeeded |
| Network down, S3 | Failed, "Lost the destination: it could not be reached for 94 s ..." | 99 s after the cut | Succeeded |
| Full SFTP destination | Failed, "Lost the destination: it is full and refused every write for 90 s ..." | 94 s after it was full | Succeeded |
| Kopia server killed | Failed, "The backup engine stopped during the run (its process ended unexpectedly) ..." | 10 s after the kill | Succeeded |
| Network back after 60 s (SFTP, S3) | CompletedWithWarnings, 21 and 16 files not backed up (below the 90 s window, as intended) | by itself | Succeeded |
| Network back after 5 s (SFTP, S3) | Succeeded, restored byte for byte | by itself | Succeeded |
| Agent killed | Failed, "The agent stopped during this run ..." | at the agent's restart | Succeeded |

Known limits:

- A network that drops packets instead of refusing them (no reset) makes each attempt wait for TCP timeouts (21 s to
  connect on Windows): the first give-up, and so the end of the run, can then take several minutes. Not reproduced:
  the chaos proxy resets connections.
- The task log keeps 1,000 lines; a burst of failing small files can push storage errors out between two reads. An
  outage keeps adding new ones, so the rule still fires, possibly later.
- Restores are not watched this way; a restore whose destination goes away ends when Kopia gives up.
- The local disk-full wording is Windows' "There is not enough space on the disk." (unit-tested, not observed: the
  VHDX test needs an elevated run).

## Open

- **Finding 6** needs a reproduction with the workspace kept (`CASHGEAR_KEEP_TEST_WORKSPACE=1`) to see whether the pack
  file is missing, held open, or refused by SFTPGo itself (its quota handling deletes rejected uploads), and whether a
  real NAS can do the same. Until then, a failing `A_full_sftp_destination...` run is not proof of a product bug.
- **The local disk-full test** has not been run here (the session was not elevated). Run it once from an elevated
  prompt: `dotnet test --project tests\CashGear.Backup.Hardening.Tests --explicit only --filter-method "*A_full_local_disk*"`.
- **Findings 2 and 3 are fixed but need hand review** (rule 10, see above), and the 90 s window should be checked on a
  real NAS and a real S3 service during the beta: unplug the network, pull a USB disk, fill a share.
- **The 200 GB restore and the 1M-file byte-compare** were not completed in the first full run (hours on a hard disk;
  see the results). Run them on the release machine with `CASHGEAR_SCALE_RESTORE` on, ideally with the destination
  and restore target on different disks.
