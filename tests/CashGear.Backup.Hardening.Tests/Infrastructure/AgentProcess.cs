using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using CashGear.Backup.Contracts;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Testing;
using Grpc.Core;
using Grpc.Net.Client;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// The real agent as its own process (the console form of the service, from its build output), with its data in a
/// test workspace and the pinned kopia.exe, driven over its named pipe like the desktop app. Unlike the in-process
/// harness of the agent tests, it can be killed: <see cref="KillAsync"/> terminates the agent process alone, the way
/// a crash or power loss would, and leaves the kopia server to the engine's own Job Object.
/// </summary>
internal sealed class AgentProcess : IAsyncDisposable
{
    public const string KopiaProcessName = "kopia";
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentQueue<string> _output = new();
    private ProcessJob? _job;
    private Process? _process;
    private GrpcChannel? _channel;

    private AgentProcess(Workspace workspace) => Workspace = workspace;

    public Workspace Workspace { get; }
    public string PipeName { get; } = "cashgear-hardening-" + RandomNumberGenerator.GetHexString(12, lowercase: true);

    /// <summary>The repository password of every destination the tests create. Never print it.</summary>
    public string Password { get; } = RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 24);

    public BackupAgent.BackupAgentClient Client { get; private set; } = null!;
    public Process Process => _process ?? throw new InvalidOperationException("The agent is not running.");
    public ProcessJob Job => _job ?? throw new InvalidOperationException("The agent is not running.");
    public bool IsRunning => _process is { HasExited: false };

    public static string ExecutablePath { get; } = FindExecutable();

    public static async Task<AgentProcess> StartAsync(Workspace workspace, CancellationToken ct)
    {
        var agent = new AgentProcess(workspace);
        try
        {
            await agent.StartAsync(ct);
            return agent;
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts the agent process (again, after a kill) on the same data folder and pipe, and waits until it is ready.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The agent is already running.");
        }

        ReleaseProcess();
        var start = new ProcessStartInfo(ExecutablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Agent__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["Agent__DataDirectory"] = Workspace.Data;
        start.Environment["Agent__PipeName"] = PipeName;
        start.Environment["Agent__EngineExecutablePath"] = KopiaTestBinary.ExePath;
        start.Environment["Agent__AllowNonElevatedManage"] = "true";
        start.Environment["Agent__LicenceEnforcement"] = "false";
        start.Environment["Agent__WriteToConsole"] = "false";
        start.Environment["Agent__AutomaticUpdates"] = "false";
        start.Environment["Agent__HousekeepingInterval"] = "00:00:00";

        var job = new ProcessJob();
        var process = Process.Start(start) ?? throw new InvalidOperationException("The agent did not start.");
        _job = job;
        _process = process;
        job.Assign(process);
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _channel = AgentChannel.Create(new AgentChannelOptions { PipeName = PipeName, ConnectTimeout = TimeSpan.FromSeconds(2) });
        Client = new BackupAgent.BackupAgentClient(_channel);
        await WaitReadyAsync(ct);
    }

    private async Task WaitReadyAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (Process.HasExited)
            {
                throw new InvalidOperationException($"The agent exited with code {Process.ExitCode} while starting:{Environment.NewLine}{OutputTail()}");
            }

            try
            {
                if ((await Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: ct)).Ready)
                {
                    return;
                }
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal)
            {
                // The pipe is not there yet.
            }

            if (stopwatch.Elapsed > ReadyTimeout)
            {
                throw new TimeoutException($"The agent was not ready after {ReadyTimeout}:{Environment.NewLine}{OutputTail()}");
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// Terminates the agent process only (TerminateProcess, no shutdown code runs), like a crash or a power cut of
    /// the service. Its kopia server must go with it through the engine's Job Object; returns those server processes
    /// so the caller can check.
    /// </summary>
    public async Task<IReadOnlyList<Process>> KillAsync()
    {
        var kopia = Processes(KopiaProcessName);
        _channel?.Dispose();
        _channel = null;
        Process.Kill(entireProcessTree: false);
        await Process.WaitForExitAsync(CancellationToken.None);
        return kopia;
    }

    /// <summary>Running processes of the agent's tree with this name (the agent, <c>kopia</c>).</summary>
    public IReadOnlyList<Process> Processes(string name)
    {
        var found = new List<Process>();
        foreach (var id in Job.ProcessIds())
        {
            try
            {
                var process = Process.GetProcessById(id);
                if (string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (ArgumentException)
            {
                // Exited between the listing and the lookup.
            }
        }

        return found;
    }

    public string OutputTail() => string.Join(Environment.NewLine, _output.TakeLast(40));

    /// <summary>The agent's own log files (Serilog), for diagnostics.</summary>
    public string ReadLogs()
    {
        var logs = Path.Combine(Workspace.Data, "logs");
        return !Directory.Exists(logs) ? "" : string.Concat(Directory.GetFiles(logs).Order().Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    // --- API helpers -------------------------------------------------------------------------------------------

    public async Task<Destination> CreateDestinationAsync(CreateDestinationRequest request, CancellationToken ct)
    {
        request.Password = Password;
        return await Client.CreateDestinationAsync(request, cancellationToken: ct);
    }

    public Task<Destination> CreateFolderDestinationAsync(string folder, CancellationToken ct) =>
        CreateDestinationAsync(new CreateDestinationRequest { Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = folder }, ct);

    public async Task<Job> CreateJobAsync(string destinationId, string source, CancellationToken ct) =>
        await Client.CreateJobAsync(new CreateJobRequest
        {
            Spec = new JobSpec
            {
                Name = "Documents",
                DestinationId = destinationId,
                SourcePath = source,
                Compression = Compression.Balanced,
                Retention = new Retention { KeepLatest = 10, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, KeepAnnual = 2 },
                Schedule = new Schedule { Manual = new ManualSchedule() },
                Enabled = true,
            },
        }, cancellationToken: ct);

    public async Task<Run> StartBackupAsync(string jobId, CancellationToken ct) =>
        await Client.RunJobNowAsync(new RunJobNowRequest { JobId = jobId }, cancellationToken: ct);

    /// <summary>Follows a run until <paramref name="until"/> holds for an update (returns it) or the run ends (returns the finished run).</summary>
    public async Task<(RunProgress? Matched, Run? Finished)> WatchUntilAsync(string runId, Func<RunProgress, bool> until, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var call = Client.WatchRun(new WatchRunRequest { RunId = runId }, cancellationToken: limit.Token);
        await foreach (var update in call.ResponseStream.ReadAllAsync(limit.Token))
        {
            if (update.Finished is { } finished)
            {
                return (null, finished);
            }

            if (until(update))
            {
                return (update, null);
            }
        }

        throw new InvalidOperationException($"Watching run {runId} ended without its result.");
    }

    public async Task<Run> WatchToEndAsync(string runId, TimeSpan timeout, CancellationToken ct) =>
        (await WatchUntilAsync(runId, _ => false, timeout, ct)).Finished!;

    public async Task<Run> BackupAsync(string jobId, TimeSpan timeout, CancellationToken ct) =>
        await WatchToEndAsync((await StartBackupAsync(jobId, ct)).Id, timeout, ct);

    /// <summary>The agent's check of the job's backups, reading back every file (not the 5 % default).</summary>
    public async Task<Run> VerifyAllAsync(string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var settings = await Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: ct);
        if (settings.VerifySamplePercent < 100)
        {
            settings.VerifySamplePercent = 100;
            await Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = settings }, cancellationToken: ct);
        }

        var run = await Client.VerifyJobNowAsync(new VerifyJobNowRequest { JobId = jobId }, cancellationToken: ct);
        return await WatchToEndAsync(run.Id, timeout, ct);
    }

    public async Task<Run> RestoreAsync(string jobId, string snapshotId, string target, TimeSpan timeout, CancellationToken ct)
    {
        var run = await Client.StartRestoreAsync(new StartRestoreRequest
        {
            JobId = jobId, SnapshotId = snapshotId, TargetFolder = target, Location = RestoreLocation.NewFolder,
        }, cancellationToken: ct);
        return await WatchToEndAsync(run.Id, timeout, ct);
    }

    public async Task<IReadOnlyList<Run>> ListRunsAsync(string jobId, CancellationToken ct) =>
        (await Client.ListRunsAsync(new ListRunsRequest { JobId = jobId, Limit = 100 }, cancellationToken: ct)).Runs;

    public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string jobId, CancellationToken ct) =>
        (await Client.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = jobId }, cancellationToken: ct)).Snapshots;

    public ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        _channel = null;
        ReleaseProcess();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the job (terminating whatever is left in it) and forgets the process.</summary>
    private void ReleaseProcess()
    {
        _job?.Dispose();
        _job = null;
        _process?.Dispose();
        _process = null;
    }

    private void Remember(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        while (_output.Count > 200)
        {
            _output.TryDequeue(out _);
        }
    }

    private static string FindExecutable()
    {
        var assembly = typeof(AgentProcess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CashGear.AgentAssembly")?.Value;
        var exe = string.IsNullOrEmpty(assembly) ? null : Path.ChangeExtension(assembly, ".exe");
        return exe is not null && File.Exists(exe)
            ? exe
            : throw new InvalidOperationException($"The agent's build output was not found ('{exe}'). Build the solution first.");
    }
}
