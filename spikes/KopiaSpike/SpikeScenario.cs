using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KopiaSpike;

public sealed record SpikeOptions(string KopiaExe, string KopiaVersion, bool KeepWorkspace, string ArtifactsRoot);

/// <summary>
/// Phase 0: start kopia server, create a filesystem repository, back up, list snapshots, restore,
/// byte-compare; then probe the edges the product depends on. Each step adds a check or an observation.
/// </summary>
public static class SpikeScenario
{
    private const string UserName = "cashgear";
    private const string HostName = "spike-host";
    private static readonly TimeSpan s_startTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_taskTimeout = TimeSpan.FromMinutes(2);

    public static async Task<SpikeReport> RunAsync(SpikeOptions options, CancellationToken ct)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{RandomNumberGenerator.GetHexString(4, lowercase: true)}";
        using var ws = SpikeWorkspace.Create(runId, options.ArtifactsRoot);
        ws.Keep = options.KeepWorkspace;

        var report = new SpikeReport { RunId = runId, KopiaVersion = options.KopiaVersion, ArtifactsDirectory = ws.Artifacts };
        var recorder = new ExchangeRecorder(ws.Http, new Dictionary<string, string>
        {
            [ws.Root] = "<workspace>",
            [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] = "<userprofile>",
        });
        var secrets = SpikeSecrets.Generate();
        var allSecrets = new List<string>(secrets.Passwords);

        try
        {
            await RunStepsAsync(options, ws, recorder, secrets, allSecrets, report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            report.Fatal = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Last, so it covers every log and capture written above.
        ScanForLeakedSecrets(ws, allSecrets, report);

        if (!report.Passed)
        {
            ws.Keep = true;
            report.Observe("workspace.keptForInspection", recorder.Scrub(ws.Root));
        }

        await File.WriteAllTextAsync(Path.Combine(ws.Artifacts, "summary.json"), recorder.Scrub(report.ToJson()), CancellationToken.None);
        return report;
    }

    private static async Task RunStepsAsync(
        SpikeOptions options, SpikeWorkspace ws, ExchangeRecorder recorder, SpikeSecrets secrets,
        List<string> allSecrets, SpikeReport report, CancellationToken ct)
    {
        // 1. First start: no repository config yet.
        var cert = SpikeCertificate.Create(ws.Tls);
        report.Observe("server.arguments", KopiaServerProcess.BuildArguments(ws, cert).Select(recorder.Scrub).ToList());
        var server = await KopiaServerProcess.StartAsync(new(options.KopiaExe, ws, cert, secrets, PassRepositoryPassword: false, "server-1"), s_startTimeout, ct);
        using var api = new KopiaApiClient(server.BaseAddress, cert.Pin, secrets, recorder);
        var workflows = new KopiaWorkflows(api);
        SourceInfo? source;
        List<string> snapshotIdsBeforeRestart;
        try
        {
            var initial = await api.WaitUntilReadyAsync(s_startTimeout, ct);
            report.Observe("server-1.addressFormat", server.BaseAddress.Scheme + "://127.0.0.1:<port>");
            report.Observe("server-1.controlStatusBeforeRepo", new { initial.Connected });
            report.Observe("tls.negotiated", await DescribeTlsAsync(server.BaseAddress, cert.Pin, ct));

            // 2. Auth, CSRF and certificate pinning.
            var noCsrf = await api.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, includeCsrf: false, ct);
            report.Check("csrf.required", noCsrf.Status == 401, $"UI call without CSRF token: HTTP {noCsrf.Status} {noCsrf.Body.Trim()}");

            await api.InitializeCsrfAsync(ct);
            var withCsrf = await api.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, includeCsrf: true, ct);
            report.Check("csrf.accepted", withCsrf.Status == 200, $"UI call with cookie + CSRF token: HTTP {withCsrf.Status}");

            var controlOnUi = await ControlUserUiAccessAsync(server.BaseAddress, cert.Pin, secrets, ct);
            report.Check("auth.control-user-cannot-use-ui-api", controlOnUi.UiStatus is 401 or 403,
                $"control user: GET / -> HTTP {controlOnUi.PageStatus} (CSRF token issued: {controlOnUi.GotToken}); " +
                $"UI endpoint with its own session -> HTTP {controlOnUi.UiStatus} {controlOnUi.UiBody}");

            report.Check("tls.pin-mismatch-rejected", await IsRejectedWithWrongPinAsync(server.BaseAddress, secrets, ct),
                "a client pinned to a different certificate must fail the TLS handshake");

            report.Observe("metrics.withoutAuth", await StatusWithoutAuthAsync(server.BaseAddress, cert.Pin, "/metrics", ct));

            // 3. Repository and source.
            // The create response is the status from before the connection (connected=false); poll for it.
            var created = await api.CreateFilesystemRepositoryAsync(ws.Repo, secrets.RepositoryPassword, UserName, HostName, ct);
            report.Observe("repo.createResponse.connected", created.Connected);
            var repoStatus = await WaitForConnectedAsync(api, ct);
            report.Check("repo.create", repoStatus.Connected, $"connected={repoStatus.Connected}, storage={repoStatus.Storage}, format v{repoStatus.FormatVersion}");
            report.Observe("repo.status", repoStatus);

            var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ws.Source));
            TreeFixture.Create(sourcePath);
            var manifest1 = TreeManifest.Capture(sourcePath);
            report.Observe("fixture", new { files = manifest1.FileCount, directories = manifest1.DirectoryCount, bytes = manifest1.TotalBytes });

            using var policy = JsonDocument.Parse("""{"scheduling":{"manual":true}}""");
            report.Observe("source.createResponse", await api.CreateSourceAsync(sourcePath, policy.RootElement, ct));
            var sources = await api.ListSourcesAsync(ct);
            source = sources.Sources.Select(s => s.Source).FirstOrDefault(s => s.Path == sourcePath);
            report.Observe("sources.identity", new { sources.LocalUsername, sources.LocalHost, sources.MultiUser });
            report.Check("source.registered", source is not null, $"{sources.Sources.Count} source(s); ours present: {source is not null}");
            if (source is null)
            {
                return;
            }

            // 4. Backup #1, restore, byte-compare: the Phase 0 exit gate.
            var backup1 = await workflows.BackupAsync(source, s_taskTimeout, ct);
            CheckBackup(report, "backup.1", backup1);
            if (backup1.Snapshot is null)
            {
                return;
            }

            var restore1 = ws.RestoreTarget("r1-snapshot1");
            var restoreTask1 = await workflows.RestoreAsync(backup1.Snapshot.RootId, restore1, fullDepth: true, s_taskTimeout, ct);
            report.Check("restore.1.task", restoreTask1.Status == "SUCCESS", $"{restoreTask1.Status} {restoreTask1.ErrorMessage}");
            CheckRoundTrip(report, "roundtrip.snapshot1", manifest1, restore1);
            report.Observe("restore.1.metadata", manifest1.MetadataReport(TreeManifest.Capture(restore1)));

            // 5. Incremental backup #2, then latest and point-in-time restores.
            TreeFixture.Mutate(sourcePath);
            var manifest2 = TreeManifest.Capture(sourcePath);
            var backup2 = await workflows.BackupAsync(source, s_taskTimeout, ct);
            CheckBackup(report, "backup.2", backup2);
            if (backup2.Snapshot is null)
            {
                return;
            }

            var restore2 = ws.RestoreTarget("r2-latest");
            await workflows.RestoreAsync(backup2.Snapshot.RootId, restore2, fullDepth: true, s_taskTimeout, ct);
            CheckRoundTrip(report, "roundtrip.latest", manifest2, restore2);

            var restore3 = ws.RestoreTarget("r3-point-in-time");
            await workflows.RestoreAsync(backup1.Snapshot.RootId, restore3, fullDepth: true, s_taskTimeout, ct);
            CheckRoundTrip(report, "roundtrip.point-in-time", manifest1, restore3);

            // 6. Restoring into a non-empty folder with all overwrite flags off must fail and change nothing.
            var beforeOverwrite = TreeManifest.Capture(restore1);
            var overwriteTask = await workflows.RestoreAsync(backup2.Snapshot.RootId, restore1, fullDepth: true, s_taskTimeout, ct);
            var changed = beforeOverwrite.ContentDifferences(TreeManifest.Capture(restore1));
            report.Check("restore.never-overwrites", overwriteTask.Status == "FAILED" && changed.Count == 0,
                $"task {overwriteTask.Status}: {overwriteTask.ErrorMessage}; changes in target: {changed.Count}");

            // 7. Observation: Kopia's REST default for restore depth (what the product must never send).
            var shallow = ws.RestoreTarget("r5-shallow-default");
            var shallowTask = await workflows.RestoreAsync(backup2.Snapshot.RootId, shallow, fullDepth: false, s_taskTimeout, ct);
            var shallowFiles = Directory.Exists(shallow) ? Directory.GetFiles(shallow, "*", SearchOption.AllDirectories) : [];
            report.Observe("restore.depthOmitted", new
            {
                shallowTask.Status,
                placeholderFiles = shallowFiles.Count(f => f.EndsWith(".kopia-entry", StringComparison.Ordinal)),
                otherFiles = shallowFiles.Count(f => !f.EndsWith(".kopia-entry", StringComparison.Ordinal)),
                sample = shallowFiles.Take(3).Select(f => Path.GetRelativePath(shallow, f)).ToList(),
            });

            snapshotIdsBeforeRestart = [.. (await api.ListSnapshotsAsync(source, ct)).Snapshots.Select(s => s.Id).Order(StringComparer.Ordinal)];
        }
        finally
        {
            var stopwatch = Stopwatch.StartNew();
            var method = await server.ShutdownAsync(api.ControlShutdownAsync, ct);
            report.Check("shutdown.graceful", method == "control-api", $"stopped by {method} in {stopwatch.ElapsedMilliseconds} ms, exit code {server.ExitCode}");
            await server.DisposeAsync();
        }

        // 8. Restart as the agent would: new certificate and server credentials, password via KOPIA_PASSWORD.
        var secrets2 = secrets.WithNewServerCredentials();
        allSecrets.AddRange(secrets2.Passwords);
        var cert2 = SpikeCertificate.Create(ws.Tls);
        var server2 = await KopiaServerProcess.StartAsync(new(options.KopiaExe, ws, cert2, secrets2, PassRepositoryPassword: true, "server-2"), s_startTimeout, ct);
        using (var api2 = new KopiaApiClient(server2.BaseAddress, cert2.Pin, secrets2, recorder))
        {
            try
            {
                await api2.WaitUntilReadyAsync(s_startTimeout, ct);
                await api2.InitializeCsrfAsync(ct);
                var status = await api2.GetRepoStatusAsync(ct);
                var ids = (await api2.ListSnapshotsAsync(source, ct)).Snapshots
                    .Select(s => s.Id).Order(StringComparer.Ordinal).ToList();
                report.Check("restart.with-env-password", status.Connected && ids.SequenceEqual(snapshotIdsBeforeRestart),
                    $"connected={status.Connected}; snapshots before restart {snapshotIdsBeforeRestart.Count}, after {ids.Count}");
            }
            finally
            {
                await server2.ShutdownAsync(api2.ControlShutdownAsync, ct);
                await server2.DisposeAsync();
            }
        }

        // 9. Observation: a start with an existing config but no KOPIA_PASSWORD.
        report.Observe("start.withoutPassword", await DescribeStartWithoutPasswordAsync(options, ws, recorder, allSecrets, ct));

        // 10. Nothing may persist the repository password.
        var passwordFiles = Directory.GetFiles(ws.Root, "*.kopia-password", SearchOption.AllDirectories);
        report.Check("secrets.no-password-file", passwordFiles.Length == 0,
            passwordFiles.Length == 0 ? "no *.kopia-password files" : string.Join(", ", passwordFiles.Select(recorder.Scrub)));

        var credentialEntries = (await RunAsync("cmdkey", "/list", ct))
            .Where(l => l.Contains("repository.config", StringComparison.OrdinalIgnoreCase) || l.Contains("kopia", StringComparison.OrdinalIgnoreCase))
            .ToList();
        report.Check("secrets.no-credential-manager-entry", credentialEntries.Count == 0,
            credentialEntries.Count == 0 ? "cmdkey /list shows no Kopia entries" : string.Join("; ", credentialEntries));
    }

    private static void CheckBackup(SpikeReport report, string name, BackupOutcome outcome)
    {
        report.Check(name, outcome.Succeeded,
            $"task {outcome.Task?.Status ?? "not found"}; new snapshots {outcome.NewSnapshotIds.Count}; " +
            $"numFailed {outcome.Snapshot?.Summary?.NumFailed}; incomplete '{outcome.Snapshot?.IncompleteReason}'");
        report.Observe(name, new
        {
            uploadResponse = outcome.UploadResponse,
            task = outcome.Task is null ? null : new { outcome.Task.Kind, outcome.Task.Description, outcome.Task.Status, outcome.Task.Counters },
            sourceStatusTimeline = outcome.SourceStatusTimeline,
            summary = outcome.Snapshot?.Summary,
            elapsedMs = (int)outcome.Elapsed.TotalMilliseconds,
        });
    }

    private static void CheckRoundTrip(SpikeReport report, string name, TreeManifest expected, string restoredPath)
    {
        var differences = Directory.Exists(restoredPath)
            ? expected.ContentDifferences(TreeManifest.Capture(restoredPath))
            : ["restore target does not exist"];
        report.Check(name, differences.Count == 0,
            differences.Count == 0
                ? $"{expected.FileCount} files, {expected.DirectoryCount} directories, {expected.TotalBytes} bytes identical (SHA-256)"
                : string.Join("; ", differences.Take(10)) + (differences.Count > 10 ? $"; ... {differences.Count} total" : ""));
    }

    private static async Task<RepoStatus> WaitForConnectedAsync(KopiaApiClient api, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var status = await api.GetRepoStatusAsync(ct);
            if (status.Connected || stopwatch.Elapsed > s_startTimeout)
            {
                return status;
            }

            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// Gives the control user every chance at the UI API: its own session cookie and CSRF token from
    /// GET /, then a UI endpoint. Authorization, not just CSRF, must stop it.
    /// </summary>
    private static async Task<(int PageStatus, bool GotToken, int UiStatus, string UiBody)> ControlUserUiAccessAsync(
        Uri address, byte[] pin, SpikeSecrets secrets, CancellationToken ct)
    {
        using var client = KopiaApiClient.CreateHttpClient(address, pin, secrets.ControlUser, secrets.ControlPassword, useCookies: true);
        using var page = await client.GetAsync("/", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        var token = System.Text.RegularExpressions.Regex.Match(html, "kopia-csrf-token\" content=\"([^\"]+)\"");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/repo/status");
        if (token.Success)
        {
            request.Headers.Add("X-Kopia-Csrf-Token", token.Groups[1].Value);
        }

        using var response = await client.SendAsync(request, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
        return ((int)page.StatusCode, token.Success, (int)response.StatusCode, body);
    }

    private static async Task<object> DescribeTlsAsync(Uri address, byte[] pin, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, ct);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => SpikeCertificate.Matches(cert, pin));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address.Host }, ct);
        return new { protocol = ssl.SslProtocol.ToString(), cipherSuite = ssl.NegotiatedCipherSuite.ToString() };
    }

    private static async Task<bool> IsRejectedWithWrongPinAsync(Uri address, SpikeSecrets secrets, CancellationToken ct)
    {
        using var client = KopiaApiClient.CreateHttpClient(address, RandomNumberGenerator.GetBytes(32), secrets.ControlUser, secrets.ControlPassword, useCookies: false);
        try
        {
            using var response = await client.GetAsync("/api/v1/control/status", ct);
            return false;
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return true;
        }
    }

    private static async Task<int> StatusWithoutAuthAsync(Uri address, byte[] pin, string path, CancellationToken ct)
    {
        using var client = KopiaApiClient.CreateHttpClient(address, pin, null, null, useCookies: false);
        using var response = await client.GetAsync(path, ct);
        return (int)response.StatusCode;
    }

    private static async Task<object> DescribeStartWithoutPasswordAsync(
        SpikeOptions options, SpikeWorkspace ws, ExchangeRecorder recorder, List<string> allSecrets, CancellationToken ct)
    {
        var secrets = SpikeSecrets.Generate();
        allSecrets.AddRange(secrets.Passwords);
        var cert = SpikeCertificate.Create(ws.Tls);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var server = await KopiaServerProcess.StartAsync(new(options.KopiaExe, ws, cert, secrets, PassRepositoryPassword: false, "server-3-no-password"), TimeSpan.FromSeconds(20), ct);
            using var api = new KopiaApiClient(server.BaseAddress, cert.Pin, secrets, recorder);
            try
            {
                var status = await api.WaitUntilReadyAsync(s_startTimeout, ct);
                return new { outcome = "started", status.Connected, elapsedMs = stopwatch.ElapsedMilliseconds };
            }
            finally
            {
                await server.ShutdownAsync(api.ControlShutdownAsync, ct);
                await server.DisposeAsync();
            }
        }
        catch (KopiaServerStartException ex)
        {
            return new { outcome = "failed-to-start", ex.ExitCode, elapsedMs = stopwatch.ElapsedMilliseconds, output = ex.OutputTail.Select(recorder.Scrub).ToList() };
        }
    }

    /// <summary>Searches every artifact and Kopia file (config, logs, cache, repo) for the generated passwords.</summary>
    private static void ScanForLeakedSecrets(SpikeWorkspace ws, List<string> secrets, SpikeReport report)
    {
        var needles = secrets
            .SelectMany(s => new[] { Encoding.UTF8.GetBytes(s), Encoding.Unicode.GetBytes(s), Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(s))) })
            .ToList();
        var roots = new[] { ws.Artifacts, ws.Root };
        var skip = new[] { ws.Source, ws.Restores };
        var hits = new List<string>();
        var scanned = 0;

        foreach (var file in roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories)).Distinct())
        {
            if (skip.Any(s => file.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            scanned++;
            byte[] content;
            try
            {
                content = File.ReadAllBytes(file);
            }
            catch (IOException ex)
            {
                hits.Add($"{Path.GetFileName(file)}: unreadable ({ex.Message})");
                continue;
            }

            if (needles.Any(n => content.AsSpan().IndexOf(n) >= 0))
            {
                hits.Add(Path.GetRelativePath(ws.Root, file));
            }
        }

        report.Check("secrets.no-leaks", hits.Count == 0,
            hits.Count == 0 ? $"{scanned} files scanned, no generated password found" : "password found in: " + string.Join(", ", hits));
    }

    private static async Task<List<string>> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
