# Phase 0 findings: driving Kopia v0.23.1 over its REST API

**Result: the Phase 0 gate passes.** A console app starts `kopia server`, creates a filesystem
repository, backs up a folder, lists snapshots and restores it. Every file comes back byte-identical
(SHA-256 of all 209 files, empty files and empty directories included), for the latest snapshot and for
a point-in-time restore. All 18 checks pass; `dotnet test --solution spikes\KopiaSpike.slnx` is green
(6 tests, ~13 s).

- Environment: Windows 10 Pro 22H2 (19045), .NET SDK 10.0.402, Kopia 0.23.1 (build `72ec08f`), 2026-10-15.
- Evidence: [`samples/summary.json`](samples/summary.json) (checks and observations of one run) and
  redacted request/response captures in [`samples/`](samples/). Each run writes the full set to
  `artifacts/spike/<runId>/` (gitignored).
- Research baseline: Kopia source at tag v0.23.1 (`internal/server`, `internal/serverapi`, `cli/`).

## What differed from expectations

| # | Expected | Observed | Consequence |
|---|---|---|---|
| 1 | `POST /api/v1/repo/create` returns the connected status | Returns the status from *before* connecting: `connected:false`, empty hostname. The next `GET /repo/status` shows `connected:true` | Poll `/repo/status` after create/connect |
| 2 | `/metrics` is served without auth | `GET /metrics` without credentials returns **401** | No exposure; nothing to do |
| 3 | Find the backup's task by diffing `/tasks` | Works, but `/sources` also shows `currentTask:"<id>"` while the source is `UPLOADING` | Use `currentTask` when seen, fall back to the `/tasks` diff (fast runs finish before a poll) |
| 4 | Restore keeps file metadata | File mtimes 209/209 and ReadOnly kept; **Hidden attribute lost** (0/1); **directory mtimes not reliable** (8 to 11 of 12 kept, varies by run) | Phase 3 decision: re-apply attributes ourselves or document the limitation |
| 5 | `Uploaded Bytes` counter measures data written | First 16 MB backup reports `Uploaded Bytes: 204` while the repository grew to 24.5 MB | Show progress from `Hashed`/`Processed` counters; never `Uploaded Bytes` |
| 6 | Kopia compresses by default | Default policy has **no compression**: 24.5 MB repository for a 16.1 MB source plus one small incremental | Set `compression` (zstd) in every job policy |
| 7 | Default retention is irrelevant while scheduling is manual | Snapshots carry `retention: ["latest-1","hourly-1","daily-1",…]`: Kopia's default retention policy is active and will prune | Always write retention explicitly from the job's settings |

## Confirmed as expected

| Area | Confirmed behaviour |
|---|---|
| Launch | Flags in [Launch line](#launch-line) all accepted. `--address=127.0.0.1:0` binds a free port; `SERVER ADDRESS: https://127.0.0.1:<port>` arrives on **stderr** about 0.5 s after Kopia's first output line |
| Web UI off, API on | `--ui --html=<stub dir>` serves our stub page (Kopia injects the CSRF meta tag into it); the REST API works |
| TLS | Our .NET-generated ECDSA P-256 cert (PEM, PKCS#8 key) is accepted. Windows 10 SChannel negotiates **TLS 1.2, `TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256`**. A client pinned to another cert fails the handshake |
| Auth | HTTP Basic. Credentials come only from `KOPIA_SERVER_USERNAME/PASSWORD` and `KOPIA_SERVER_CONTROL_USER/PASSWORD` |
| CSRF | Every UI endpoint, GETs included, needs cookie `Kopia-Session-Cookie` + header `X-Kopia-Csrf-Token`. The token comes from `<meta name="kopia-csrf-token">` in `GET /`. Without it: `401 "Invalid or missing CSRF token."` (plain text) |
| User separation | The control user gets **403 on `GET /`**, so it can never obtain a CSRF token, and 401 on UI endpoints. (The reverse, the UI user on `/api/v1/control/*`, was not tested.) |
| Repository | `repo/create` with `{"storage":{"type":"filesystem","config":{"path":…}},"password","options":{},"clientOptions":{hostname,username,description}}` takes ~1 s and gives format v3, `BLAKE2B-256-128`, `AES256-GCM-HMAC-SHA256`, `DYNAMIC-4M-BUZHASH`, 20 MiB packs |
| Source identity | `/sources` reports `localUsername`/`localHost` = our `clientOptions` (`cashgear@spike-host`) and `multiUser: true`. Source paths are stored exactly as given |
| Backup trigger | `POST /sources/upload?userName&host&path` returns only `{"sources":{"user@host:path":{"success":true}}}`: no task ID, no snapshot ID |
| Finding the snapshot | Exactly one new ID in `GET /snapshots?all=1&userName&host&path` after the run (`host` required; without `all=1` identical roots collapse) |
| Run lifecycle | Source status `UPLOADING` then `IDLE` (polled at 250 ms). Task `kind:"Snapshot"`, description `user@host:path at <local time>`, statuses `RUNNING` then `SUCCESS` |
| Incremental | Second run after the mutation hashed 3 files (12.6 MB, the changed 12 MiB file is re-hashed whole), reused 206 cached files |
| Speed | 16 MB / 209 files: backup ~0.3 s, restore ~0.8 s |
| Restore | `POST /restore` returns a `kind:"Restore"` task immediately; poll `/tasks/{id}`. Unicode names, NFC vs NFD `é` as two files, emoji/CJK, a non-ASCII source path, empty files and nested empty directories all round-trip |
| Restore depth | **Omitting `restoreDirEntryAtDepth` silently restores placeholders**: task `SUCCESS`, 8 `*.kopia-entry` files, 0 real files. Sending `2147483647` restores everything |
| No overwrite | Restore into a non-empty folder with all `overwrite*` flags false fails (`non-empty directory already exists, not overwriting it`) and leaves the folder untouched |
| Shutdown | `POST /api/v1/control/shutdown` (control user, no CSRF) returns `{}`; the process exits with code 0 in 50 to 70 ms |
| Restart | New cert and new server credentials plus `KOPIA_PASSWORD`: connects synchronously at start, same snapshot IDs |
| No password | Existing config without `KOPIA_PASSWORD`: exits with code 1 in ~0.6 s, `password prompt error: The handle is invalid.` |
| Secrets | With `--no-persist-credentials --no-use-credential-manager`: no `*.kopia-password` file, no Credential Manager entry, and none of the generated passwords (UTF-8, UTF-16 or base64) in 322 files: captures, server logs, Kopia logs, config, cache and repository |
| Errors | JSON `{"code","error"}` bodies are sent as `text/plain`; 401s are plain text |

## Launch line

```
kopia.exe server start
  --config-file=<data>\repos\<id>\repository.config --cache-directory=<data>\…\cache --log-dir=<data>\…\logs
  --address=127.0.0.1:0 --tls-cert-file=<…>\server.crt --tls-key-file=<…>\server.key
  --ui --html=<stub dir> --no-grpc --shutdown-on-stdin
  --no-persist-credentials --no-use-credential-manager

environment: inherited minus every KOPIA_* variable, plus
  KOPIA_SERVER_USERNAME, KOPIA_SERVER_PASSWORD, KOPIA_SERVER_CONTROL_USER, KOPIA_SERVER_CONTROL_PASSWORD,
  KOPIA_CHECK_FOR_UPDATES=false, and KOPIA_PASSWORD once the config exists
stdin: redirected and held open for the server's lifetime
```

Kopia still prints "Server will allow connections from users whose accounts are stored in the
repository" with `--no-grpc`; it is harmless.

## Phase 1 to-do (from these findings)

All done in Phase 1a except where noted; the code is in `src/CashGear.Backup.Engine.Kopia`.

1. **KopiaProcessHost:** done (`Hosting/`). Job Object with kill-on-close, health check every 30 s
   (restart at once if the process exited, after 2 failed checks otherwise), restart with back-off
   1 s to 60 s, SHA-256 of kopia.exe checked against the pin *before every start*. The pin is embedded
   in the assembly at build time, and kopia.exe is held open deny-write from the check until the
   process exists. `--shutdown-on-stdin` stopping Kopia when the agent dies is still untested; the
   Job Object covers that case and is tested.
2. **REST client:** done (`Api/`). No proxy, pinned TLS, CSRF re-fetched once on a 401.
3. **Create/connect** poll `/repo/status`: done.
4. **Backup run** rule 5: done (`Runs/BackupRunClassifier.cs`), extended with `numIgnoredErrors`, the
   task's `Errors` counter, and canceled and checkpoint snapshots (Phase 1a #5 and #6 below).
5. **Policy per source:** done (`Policy/`), written whole, read back, and re-verified before every backup.
6. **Restore:** done. The contract test `Kopia_default_restore_depth_writes_placeholders_…` keeps the
   hazard visible, and the engine fails a restore that left any `*.kopia-entry` file.
7. **Progress:** done, from the `Processed`/`Hashed` counters plus the source's size estimate.
8. **Error text** with absolute paths: unchanged; still to scrub before anything leaves the machine
   (Phase 4 support bundle).
9. **Identity:** `KopiaEngineOptions.ClientHostName` must be a stable id from state.db (Phase 1b).
10. **TLS key file:** regenerated on every start and deleted as soon as the server answers over TLS.

## Phase 1a: what the engine work found

Observed against the same pinned binary while building the engine (2026-10-15). Each row is covered by
a test in `tests/CashGear.Backup.Engine.Kopia.IntegrationTests`.

| # | Observed | Consequence |
|---|---|---|
| 1 | `PUT /api/v1/policy` **replaces** the whole policy at that level: a PUT with only `retention.keepLatest` clears compression, scheduling and the other retention fields, which then inherit the global defaults | Always PUT every section we own |
| 2 | Unknown policy fields (a typo like `keepLatst`) are **dropped without an error**; a wrong type (VSS `"when-available"` as a string) gives `400 MALFORMED_REQUEST` | Read the policy back after every write and before every backup |
| 3 | Any `compressorName` is accepted, even `zstd-nope`. `GET /repo/algorithms` lists the valid ones (`none` is valid but unlisted; `lz4` and `zstd-best-compression` are deprecated) | The engine checks the name against `/repo/algorithms`; we use `zstd-fastest`, `zstd` and `zstd-better-compression` |
| 4 | Global defaults at v0.23.1: retention 10 latest / 48 hourly / 7 daily / 4 weekly / 24 monthly / 3 annual, compression `none`, VSS never, `ignoreDotFiles: [".kopiaignore"]`. An empty list counts as unset, so `.kopiaignore` cannot be switched off with `[]` | We set `ignoreDotFiles: [".cashgearignore"]` so a stray `.kopiaignore` never excludes data |
| 5 | **A file that cannot be read** (sharing violation or access denied): task `SUCCESS`, a complete snapshot with `numFailed: 1` and `summary.errors: [{path, error}]` (path relative to the source). With `ignoreFileErrors: true` the same run shows `numFailed: 0`, `numIgnoredErrors: 1` and task counter `Errors: 0` | `ignoreFileErrors` stays false, and rule 5 also requires `numIgnoredErrors == 0`; such runs are `CompletedWithWarnings` with the list |
| 6 | **Cancel** (`POST /sources/cancel`): the task still ends `SUCCESS`, and Kopia saves a snapshot with `incomplete: "canceled"`. The cancel waits for in-flight pack uploads; a cancel sent before the uploader exists is lost | The classifier maps it to Canceled with no snapshot ID; the engine re-sends the cancel every second until the task ends |
| 7 | Retention is applied by the server right after each snapshot. With `ignoreIdenticalSnapshots: false` an unchanged source still records a snapshot | Each successful run has exactly one new snapshot. `RetentionRule.KeepLatest` must be at least 1, or the new snapshot could be pruned at once |
| 8 | **The server runs full maintenance on its own** about 1 s after it opens a repository it owns (after `repo/connect`, and at start with an existing config) when maintenance is due | Contradicts the plan's "our Quartz jobs run maintenance". Decision needed, see below |
| 9 | `GET /api/v1/objects/{id}` with a malformed ID makes the handler **panic** (empty reply, connection dropped); a well-formed unknown ID gives 404; `rootID/sub` paths give 404 | The client validates IDs (`^I*[g-z]?[0-9a-f]{16,}$`) and walks folders entry by entry |
| 10 | `POST /restore` accepts `root: "<rootID>/sub/path"`; for a file, `targetPath` is the file to write | Subfolder and single-file restores without an extra API |
| 11 | Repository errors are HTTP 400 with stable codes: `INVALID_PASSWORD`, `NOT_INITIALIZED`, `ALREADY_INITIALIZED`, `ALREADY_CONNECTED`, `STORAGE_CONNECTION`. `repo/connect` also answers `connected:false` | Mapped to `BackupErrorKind`; poll after connect too |
| 12 | Server start with an existing config and a wrong `KOPIA_PASSWORD`: exit code 1 with `invalid repository password` before the address line | The host reports `InvalidPassword`; the health check never retries it |
| 13 | `PUT /repo/throttle {"maxUploadSpeedBytesPerSecond": n}` works and is **persisted** in `repository.config` (`throttlingLimits`) | Tests use it to make cancel deterministic; a future bandwidth setting must reset it explicitly |
| 14 | VSS `when-available` without elevation logs `vss: do not have Administrators group privileges` and falls back at once | No delay in non-elevated tests; locked files under LocalSystem are still to test (Phase 1b) |
| 15 | Kopia appends its version to the `<title>` of our stub page (`CashGear v0.23.1`) and injects the CSRF meta tag | Harmless |
| 16 | The private key can be deleted once the server answers over TLS; requests keep working | The key file lives for about a second per start |
| 17 | A running `kopia.exe` can be renamed but not written | The pin check before every start catches a binary swapped while the old one runs (tested) |
| 18 | `Uploaded Bytes` was 28 MB in a throttled run against 204 B in Phase 0 for similar data | Confirms Phase 0 #5: never show it |

## Phase 3: verification and browsing

Observed against the same pinned binary (2026-10-15); covered by `Engine/VerifyTests.cs` and `Engine/BrowseTests.cs`.

| # | Observed | Consequence |
|---|---|---|
| 1 | No REST call verifies a repository. `kopia snapshot verify --json --sources=<user@host:path> --verify-files-percent=N` prints one JSON line on stdout: `{"stats":{"processedObjectCount","readFileCount","readBytes",...},"errorCount":n,"errorStrings":[...]}`; exit code 1 when anything is wrong | `KopiaBackupEngine.VerifyAsync` runs it through `Hosting/KopiaCommand` (pinned binary checked first, Job Object, scrubbed `KOPIA_*`, password only in the child's environment) and parses that line |
| 2 | **Reads served from the local content cache are not checked against the destination**: with a pack corrupted at the destination, verify at 100 % still passes after anything (a backup, a restore, an earlier verify) has cached the contents | Verify runs on a throwaway copy of `repository.config` whose `caching.cacheDirectory` is a new, empty folder, deleted afterwards. Then the damage shows (`invalid checksum ... cipher: message authentication failed`) |
| 3 | `--verify-files-percent=0` checks that every object of every snapshot is in the index, without reading contents; it passes with damaged packs | The share read back is a setting (default 5 %, plan) |
| 4 | The CLI verify runs fine beside the server on the same repository | No need to stop the server |
| 5 | A directory object for an **empty folder** has `"entries": null`, which replaced the DTO's empty list; `BrowseAsync` then threw (Phase 1a bug, found by the agent's snapshot search walking every folder) | Fixed in `DirectoryManifest`; `BrowseTests` covers it |

## Remote destinations: SFTP and S3

Observed against the same pinned binary (2026-10-16) with pinned local test servers (SFTPGo 2.7.6 and versitygw
1.8.0, `tools/test-servers`; MinIO's community binaries now answer 410 Gone); covered by `Engine/RemoteDestinationTests.cs`
and the agent's `RemoteDestinationTests.cs`.

| # | Observed | Consequence |
|---|---|---|
| 1 | SFTP with `knownHostsData` holding one of the server's host keys fails with `knownhosts: key mismatch` when the server offers another key type first | The desktop app reads every host key the server offers (one SSH handshake per algorithm), the user confirms the fingerprints, and all of them are stored and passed |
| 2 | A changed host key fails the connection (`knownhosts: key mismatch` / `key is unknown`) | Mapped to `BackupErrorKind.DestinationNotTrusted`; the destination must be set up again to trust a new key |
| 3 | **The SFTP password (or private key) is stored in clear in the repository's `repository.config`**; Kopia has no environment variable or other channel for it | Accepted for now: the config lives in the data folder (SYSTEM and Administrators only). Review item in docs/agent.md |
| 4 | S3 accepts empty `accessKeyID`/`secretAccessKey` in the storage config and then reads `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` from the server's environment (the CLI copies those variables into its flags, which made a first spike look as if the config held them) | Keys go only into the kopia server's and the verify command's environment; `repository.config` holds none (tested by searching the data folder). Inherited `AWS_*` variables are removed like `KOPIA_*` |
| 5 | Wrong S3 keys come back as prose (`The request signature we calculated does not match...`, `The AWS Access Key Id you provided does not exist...`), not as a status code | `KopiaBackupEngine.StorageFailure` maps those phrases to `DestinationAccessDenied`; anything else is `DestinationUnreachable` |
| 6 | Verify (Phase 3, #2) on a remote repository works the same way: a throwaway config copy with an empty cache, plus the storage environment | Same code path as folders |

## Phase 5: destination lost, server killed

Observed against the same pinned binary (2026-10-16) while fixing chaos findings 2 and 3 (docs/hardening.md): the
engine backed up 256 MB of random data at 8 MB/s and the test cut the network (every connection reset, through the
chaos proxy), filled an SFTPGo quota, or denied writes on a destination folder. Covered by `Engine/DestinationLostTests.cs`,
`Engine/EngineRestartTests.cs` and `Unit/DestinationWatchTests.cs`.

| # | Observed | Consequence |
|---|---|---|
| 1 | **A lost destination never fails the snapshot.** Kopia retries each write, then gives up on it, fails the files in that pack and goes on with the next ones. SFTP and S3: 10 retries in about 11 s; a folder: 2 retries | The engine ends such a run itself (`Runs/DestinationWatch`, below) |
| 2 | `GET /api/v1/tasks/{id}/logs` returns `{"logs":[{"level","ts","mod","msg",...}]}`, oldest first; level 0 debug to 3 error, `ts` Unix seconds. It keeps the **last 1,000 lines** (a backup of 1,500 locked files kept lines 501 to 1,500). A clean 6,000-file backup logs 15 lines | Read every 2 s; only lines newer than the last one read count |
| 3 | Each retried write: level 0, mod `retry`, `got error <error> when PutBlob(<blob>) (#N), sleeping for <d> before retrying`. Each give-up: level 3, mod `object`, `async write error: unable to write content chunk 0 of FILE:<name>: ... unable to complete PutBlob(<blob>) despite 10 retries: <error>`. A folder logs no retries and gives up as `unable to complete PutBlobInPath:<file> despite 2 retries` | `DestinationWatch.Classify` matches `Blob(`, `Blobs(`, `BlobInPath:`, `BlobFromPath:` with `despite ... retries` or `before retrying`; source read errors never match |
| 4 | First give-up after the fault: SFTP 9 s, full SFTPGo 10 s, S3 49 s, folder 3 s. Storage-failure lines then came at most **8.5 s apart** (S3; 3.8 s for SFTP) for as long as the outage lasted | 90 s window, 30 s quiet period |
| 5 | The `Errors` counter stays 0 until the files whose packs failed are finished (49 s for SFTP); `Uploaded Bytes` keeps rising during the outage (it counts finished `PutBlob` calls, failed ones too); `upload.lastError` in `/sources` stays empty | None of these can tell a lost destination; the task log can |
| 6 | Full SFTPGo: retries say `denying write due to space limit`, the give-up `cannot create temporary file: ... permission denied`, the task error `upload error: permission denied`. A folder that refuses writes: `Access is denied.` | The worst wording seen during the outage decides the message: full, then access denied, then unreachable |
| 7 | With only 64 files Kopia did end the task by itself, FAILED with `upload error: ...`, once every file had been tried (SFTP after 62 s, the folder after 60 s); S3 was still running after 4 minutes. After the engine's cancel the task ended 4 to 7 s later here (1 GB in the chaos runs: 5 s to 14 min) | The engine keeps the folder reserved until Kopia's task has ended (`DrainAsync`); a new run waits up to 30 s, then is refused with RunAlreadyActive ("still stopping") |
| 8 | **Kopia server killed mid-run**: the restart disposed the old instance's `HttpClient` under the monitor's poll, which then threw `TaskCanceledException` without a `TimeoutException` inside (so not `IsKopiaFailure`): "Following the run failed: A task was canceled." | The host's `IsCurrent(generation)` turns false before the client is disposed, and any poll failure on a replaced server reports "The backup engine stopped during the run (its process ended unexpectedly)". The health loop also wakes on `Process.Exited` and restarts at once (was: next 30 s check) |

## Not covered yet

- Running as LocalSystem in a Windows Service (cache/log locations, `%LOCALAPPDATA%` defaults avoided by flags).
- Volume Shadow Copy under LocalSystem and locked files; paths over 260 characters; symlinks and junctions.
- ~~Verification~~ done in Phase 3 (above). `RunMaintenanceAsync` stays unsupported: Kopia's server owns
  maintenance (decision 1 below).
- ~~SFTP and S3 destinations~~ done (above). SMB with its own account is tested only when
  `CASHGEAR_TEST_SMB_SHARE`/`_USER`/`_PASSWORD` name a real share.
- A server restart in the middle of a backup: the engine marks the run Failed; any checkpoint Kopia
  saved is not inspected.

## Decisions for review (Phase 1b and later)

These touch passwords, deletion, retention or restore (CLAUDE.md rule 10), or change the plan:

1. **Maintenance owner (#8):** either let the Kopia server run its own maintenance and drop the planned
   Quartz maintenance jobs, or switch Kopia's automatic maintenance off when a repository is created and
   run ours. Full maintenance deletes unreferenced data, so there must be exactly one owner.
   **Phase 3 went with the first (no code change): Kopia's server owns maintenance and the agent schedules
   none.** Reverse it only together with switching Kopia's off.
2. **Default retention** in `RetentionRule.Default`: 10 latest, 7 daily, 4 weekly, 24 monthly, 3 annual,
   no hourly (Kopia's defaults minus hourly). `KeepLatest >= 1` is enforced.
3. **Skipped files:** a run with unreadable files is `CompletedWithWarnings` (a usable snapshot exists).
   Decide whether the dashboard's "last good backup" counts it.
4. **Password lifetime:** the engine keeps a copy of each repository password in a pinned buffer, zeroed
   on dispose, for as long as the repository is connected, so it can restart Kopia. `Reveal()` makes
   short-lived managed strings for `KOPIA_PASSWORD` and the create/connect request body.
5. **`.cashgearignore`** (#4): users can exclude files by dropping this file into a folder. Document it,
   or choose a name no one will create by accident.
6. **One folder per job:** `BackupJob` has one `SourcePath` because Kopia snapshots per folder. A UI job
   with several folders maps to several engine jobs.
7. **Connecting with an existing local config** does not compare the config's storage location with the
   destination passed in; moving a destination needs its own flow (Phase 2).
8. **Ending a backup whose destination is lost (Phase 5, rule 10):** the engine records a backup Failed ("Lost the
   destination: ...") once Kopia has given up at least one write and its storage errors have gone on for 90 s
   without a 30 s pause (`KopiaEngineOptions.DestinationLossWindow`/`DestinationQuietPeriod`), and cancels Kopia's
   upload. Review: the rule only ends runs that can no longer record a complete restore point (a given-up write
   fails files); a 60 s outage stays below it (the run still ends CompletedWithWarnings, chaos finding 4); retries
   that Kopia wins never count. The folder stays reserved until Kopia's task has really ended.
