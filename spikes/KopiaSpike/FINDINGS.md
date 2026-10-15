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

1. **KopiaProcessHost:** the launch line above. Wait for `SERVER ADDRESS:`, then `GET /api/v1/control/status`.
   Stop with control shutdown, then close stdin, then kill. Add a Job Object; `--shutdown-on-stdin` should
   also stop Kopia when the agent dies (pipe closes), but that was not tested.
2. **REST client:** bootstrap CSRF with `GET /` per server start; on 401 re-fetch the token once.
   Typed DTOs from [`KopiaDtos.cs`](KopiaDtos.cs) with `[JsonExtensionData]` kept for contract tests.
3. **Repository create/connect:** poll `/repo/status` until `connected` (finding 1).
4. **Backup run:** record the snapshot ID by diffing `/snapshots?all=1`. Mark Succeeded only for task
   `SUCCESS` + exactly one new snapshot + `numFailed == 0` + no `incomplete` (CLAUDE.md rule 5).
5. **Policy per source,** written on every job change: `scheduling.manual`, compression (zstd),
   explicit retention, VSS `when-available`, error handling (findings 6 and 7).
6. **Restore:** always `restoreDirEntryAtDepth = int.MaxValue`, overwrite flags false. Keep a test that
   fails if any `*.kopia-entry` file appears.
7. **Progress UI:** hashed/processed counters only (finding 5).
8. **Error text** includes absolute paths: fine for local logs, scrub before anything leaves the machine.
9. **Identity:** pick stable `clientOptions.hostname`/`username` for the agent, because sources are keyed by
   `user@host:path` and a machine rename must not orphan them.
10. **TLS key file:** written to disk for Kopia; keep it under the SYSTEM/Administrators-only data folder and
    regenerate it on every start.

## Not covered yet

- Running as LocalSystem in a Windows Service (cache/log locations, `%LOCALAPPDATA%` defaults avoided by flags).
- Volume Shadow Copy and locked files; paths over 260 characters; symlinks and junctions.
- Behaviour when files fail (`numFailed > 0`); research says the server still reports SUCCESS, which rule 5 guards.
- Maintenance and verification: research says there is no REST endpoint (CLI against the same config), and
  that `repo/create` makes this server the maintenance owner. Both need a Phase 1 test.
- SMB, SFTP and S3 destinations.
