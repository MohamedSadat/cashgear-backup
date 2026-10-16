# The desktop app (Phase 2)

The app runs as the signed-in user. It talks to the agent only through `IAgentClient`
(`src/CashGear.Backup.Client`), which speaks the gRPC API in
[`backup_agent.proto`](../src/CashGear.Backup.Contracts/Protos/backup_agent.proto) over the agent's named pipe.

## Moving parts

| Piece | Where | What it does |
|---|---|---|
| Entry point | `Desktop/Program.cs` | One instance per user and session (a second start brings the window forward). Started with `--cashgear-elevated-helper`, the same exe runs the headless elevated helper instead |
| Shell | `Desktop/App.xaml.cs`, `MainWindow.xaml` | DI, agent status polling, BlazorWebView with the `UI.Components` pages. Closing the window keeps the tray icon; Exit ends the app and any helper |
| Tray | `Desktop/TrayIcon.cs` | Colour and tooltip from `BackupOverview`; Open, Back up now (every enabled job), Exit |
| Pages | `UI.Components/Pages` | Dashboard, job wizard (new and edit), restore, history |
| Agent client | `Client/AgentClient.cs` | The gRPC client. Errors become `AgentCallException` with the agent's `cashgear-error-kind`, or `AgentNotRunning` / `AgentNotTrusted` / `ElevationCanceled` / `ElevationFailed` |
| Pipe check | `Client/Security/AgentServerVerifier.cs` | Checks the process serving the pipe on every new connection (below) |
| Elevation | `Client/Elevation/` | `ElevatedSession` (app side) and `ElevatedHelper` (helper side) |

## Elevation (decided: a small elevated helper)

Manage calls (create or connect a destination, create, change or delete a job, start a restore) need an elevated
administrator (docs/agent.md). Before each Manage call the client asks `GetStatus`; if `caller_can_manage` is false
it sends the call through the elevated helper instead of its own connection.

**The helper is the desktop exe itself, started again with the `runas` verb and `--cashgear-elevated-helper --pipe
<random> --parent <pid>`.** It shows no window. It connects to a private pipe the app created, forwards each
Manage call to the agent on its own connection (which carries the elevated token), and sends the result back.

Why not relaunch the whole app elevated:

- **Least code elevated.** The helper is a loop over six whitelisted calls. An elevated app would run WebView2,
  its HTML and every page with administrator rights; Microsoft advises against running WebView2 elevated.
- **The user keeps their window.** Relaunching would close the running app and its tray icon (or fight the
  single-instance lock), and an elevated window cannot take drag and drop from Explorer (UIPI).
- **Folders are picked as the user.** The folder dialog runs unelevated, so it shows the user's own view.
- **One prompt per task.** The helper stays for later calls until it has been idle for 5 minutes (the app stops
  reusing it after 4), so "create destination, then create job" is one UAC prompt.

The private pipe:

- Name `CashGear.Backup.Elevation.<128 random bits>`, one instance, `FirstPipeInstance`; ACL: the user and
  Administrators may read and write (with over-the-shoulder elevation the helper runs as an administrator
  account), network logons are denied.
- The app accepts only the process it started (`GetNamedPipeClientProcessId` equals the helper's process id); the
  helper accepts only a pipe served by its parent (`GetNamedPipeServerProcessId` equals `--parent`).
- Messages: length-prefixed `ElevatedCall` / `ElevatedReply` (`Client/Protos/elevation.proto`), at most 1 MB, buffers
  zeroed after use. Nothing secret is on the command line.
- The helper exits when the app closes the pipe (Exit, or the app dies) or after 5 minutes idle.

Tests run the helper loop in-process instead of through UAC (`tests/CashGear.Backup.Client.Tests/ElevationTests.cs`):
round trip, error kinds, one helper for several calls, a declined prompt, a stranger on the pipe in either direction,
and that only Manage calls are forwarded.

## Pipe-squatting check (client side)

The agent's pipe ACL stops users from adding instances while the agent runs, but while it is stopped any process
could create a pipe with its name and wait for a repository password. So every connection the app or the helper
opens is checked before a request goes out (`AgentChannelOptions.VerifyServer`): the process at the other end must be
the process the service control manager runs for `CashGearBackupAgent`, or run as LocalSystem. Otherwise the call
fails with `AgentNotTrusted`, the banner says so, and nothing was sent. Test seam: `AgentServerTrust.ProcessIds`.

## Running it during development

Run the agent as a console app (CLAUDE.md, "Build and test") on a private pipe, then the app, Debug build, pointed at
it. Debug builds only read these two variables; Release builds always use the service's pipe and trust only the
service or LocalSystem:

```powershell
$env:CASHGEAR_AGENT_PIPE = "cashgear-dev"        # the agent's Agent__PipeName
$env:CASHGEAR_TRUST_DEV_AGENT = "1"              # the console agent runs as you, not as the service
dotnet run --project src\CashGear.Backup.Desktop
```

With `Agent__AllowNonElevatedManage=true` on the agent, Manage calls go straight through without UAC. To inspect the
page, set `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` before starting the app and use the
DevTools protocol on `http://127.0.0.1:9333/json` (Debug builds also open DevTools with F12).

## Phase 3 in the app

- **Dashboard:** an alerts panel (most serious first), "Check backups" per job with the last check's result.
  Checks are never counted as backups.
- **Restore:** restore points grouped by day; name search over the whole restore point (pick a result, or show
  its folder); restore into a new folder (default) or back where it came from (keeps both versions).
- **Settings** (`/settings`): alert and check intervals, the share of files read back, email, a test email, and
  a Recovery Kit per destination (without its password).
- **Recovery Kit:** `UI.Components/Model/RecoveryKit.cs` writes a one-page PDF by hand (standard fonts, no
  dependencies; paths and password in a fixed-width font). The wizard offers it right after creating a
  destination; the password goes on the page only when ticked. A password with characters outside Latin-1
  cannot be printed with those fonts and is left off with a note.
- **Tray:** colour and tooltip follow the most serious alert; a Windows notification for alerts not shown since
  the app started.

## Remote destinations and Phase 4 in the app

- **Wizard "where":** a folder, a network share with its own account (username and password), an SFTP server
  (password or private key; "Check the server" reads every host key as the user with SSH.NET and the user must
  confirm the fingerprints before going on) or S3-compatible storage (endpoint, region, bucket, prefix defaulting to
  the PC name, keys, HTTPS on by default). Every secret is dropped from the draft once the agent has it.
- **Licence** (Settings): edition and state, key entry (cleared from the page whether or not it was accepted),
  removal behind a confirmation. The dashboard shows a banner in the offline grace period (warning) and when lapsed
  (error). In the Free edition the wizard offers no second job and disables SFTP/S3; email alerts are disabled.
- **Updates** (Settings): status and "Check for updates"; hidden when no update source is configured.
- **Support file** (Settings): saved where the user chooses; Manage-level, so it may ask for administrator approval.
- **About**: app and service versions and `THIRD-PARTY-NOTICES.txt`, installed beside the app.

## Findings while building it

| # | Observed | Consequence |
|---|---|---|
| 1 | `Microsoft.AspNetCore.Components.WebView.Wpf` 10.0.110 uses the WebView2 composition control, which loads `Microsoft.Windows.SDK.NET`; with `net10.0-windows` the window crashes on first layout (`FileNotFoundException`) | The desktop project targets `net10.0-windows10.0.19041.0` (minimum Windows 10 1809) |
| 2 | A gRPC call whose pipe never connects (no agent, connect timeout) fails with `StatusCode.Internal` wrapping `HttpRequestException`, not `Unavailable` | `AgentCallException.FromRpc` maps any `HttpRequestException` in the chain to `AgentNotRunning` |
| 3 | Without a connect timeout, `NamedPipeClientStream.ConnectAsync` waits as long as the pipe does not exist, so a call to a stopped agent never returns | `AgentChannelOptions.ConnectTimeout` (3 s in the app) |
| 4 | Razor's `@bind` on `<input type="time">` only binds `DateTime`/`TimeOnly` | The wizard keeps "HH:mm" text with `value` + `@onchange` |
| 5 | bUnit's `ClickAsync` awaits the whole handler, so a handler that follows a run to its end never returns in a test (and blocks the UI's busy state the same way) | Starting a run returns at once; following it is a separate task, as on the dashboard |

## For review (CLAUDE.md rule 10)

1. **Passwords** live in the wizard's draft as strings (and in the password inputs) until the agent has created or
   connected the destination; then the draft drops them, also when the page is left or the user switches between
   "new" and "made earlier". They go app → private pipe → helper → agent and are never logged; the framing buffers
   are zeroed, the strings cannot be. Minimum length 8 for new repositories (the agent's rule), typed twice, with the
   warning that a lost password cannot be recovered.
2. **Deletion:** only "delete job", behind a second confirmation that says the backups stay and how to get them back
   (a new job on the same folder and destination). There is no way to delete backups or destinations in the app.
3. **Retention:** KeepLatest must stay at least 1 (UI and agent); in edit mode the page warns that lowering the
   numbers removes older backups after the next backup.
4. **Restore overwrite:** the app never offers it. The target must be a new or empty folder (the agent refuses
   anything else and the page explains); the default is a new folder `%USERPROFILE%\Restored\<job> <yyyy-MM-dd HHmm>`.
   Partial snapshots are hidden unless the user asks for them. Restored files are written by the service (LocalSystem)
   and inherit the target folder's permissions.
5. **The elevated helper lingers** up to 5 minutes after its last call. Only the app process that started it can talk
   to it, but code running inside that app could make Manage calls without a new prompt in that window (the same is
   true of an app relaunched elevated). Shorten `ElevatedHelper.IdleTimeout` if that is too long.
6. **Mapped drives:** the folder dialog turns `Z:\...` into `\\server\share\...` because the service cannot see the
   user's drive letters. The service then reaches the share as the computer account, which often has no access;
   share credentials are not supported yet.
7. **Recovery Kit with the password** (Phase 3): off by default. When ticked, the PDF holds the repository
   password in clear; the page says so next to the box. The wizard keeps the password in memory until the kit page
   is left (Done, or leaving the page), so it can be added after the fact.
8. **Email password field** (Phase 3) is write-only: sent when typed, cleared from the page after every save
   attempt, shown only as "saved".
9. **Restore back to the original folder** (Phase 3) is offered next to "into a new folder", which stays the
   default; the page explains that nothing is replaced. See docs/agent.md, review item 7.

## Not done yet

- The real UAC prompt is not exercised by automated tests (the helper runs in-process there); try it by running the
  agent without `AllowNonElevatedManage` and creating a job.
- ~~Start at sign-in and the Start menu shortcut~~ done by the MSI. The MSI does not check for or install the
  WebView2 Runtime.
- No app log file yet; unexpected errors show in the page.
- ~~SMB with credentials, SFTP and S3 destinations, notifications~~ done.
