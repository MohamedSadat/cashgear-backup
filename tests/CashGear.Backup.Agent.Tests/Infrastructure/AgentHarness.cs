using System.Diagnostics;
using System.Security.Cryptography;
using CashGear.Backup.Contracts;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Data;
using CashGear.Backup.Testing;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DestinationKind = CashGear.Backup.Contracts.V1.DestinationKind;

namespace CashGear.Backup.Agent.Tests.Infrastructure;

/// <summary>
/// The real agent hosted in-process on a private named pipe, with its data folder in %TEMP% and the pinned
/// kopia.exe, driven through <see cref="AgentChannel"/> exactly like the desktop app will.
/// </summary>
internal sealed class AgentHarness : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly Action<AgentOptions>? _configure;
    private WebApplication? _app;
    private GrpcChannel? _channel;

    private AgentHarness(string name, Action<AgentOptions>? configure)
    {
        _configure = configure;
        Root = Path.Combine(Path.GetTempPath(), "cashgear-agent-tests",
            $"{new string([.. name.Where(char.IsAsciiLetterOrDigit).Take(40)])}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}");
        foreach (var dir in new[] { Data, Destination, Source, Restores })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Destination => Path.Combine(Root, "repo");
    public string Source => Path.Combine(Root, "source-Ünï");
    public string Restores => Path.Combine(Root, "restores");
    public string PipeName { get; } = "cashgear-test-" + RandomNumberGenerator.GetHexString(12, lowercase: true);
    public string Password { get; } = RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 24);

    public BackupAgent.BackupAgentClient Client { get; private set; } = null!;
    public WebApplication App => _app ?? throw new InvalidOperationException("The agent is not running.");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<AgentHarness> StartAsync(string name, Action<AgentOptions>? configure = null)
    {
        var harness = new AgentHarness(name, configure);
        try
        {
            await harness.StartAgentAsync();
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public async Task StartAgentAsync()
    {
        _app = AgentHost.Build([], options =>
        {
            options.DataDirectory = Data;
            options.PipeName = PipeName;
            options.EngineExecutablePath = KopiaTestBinary.ExePath;
            options.AllowNonElevatedManage = true;
            options.WriteToConsole = false;
            options.LicenceEnforcement = false;
            _configure?.Invoke(options);
        });
        await _app.StartAsync(Ct);
        _channel = AgentChannel.Create(PipeName);
        Client = new BackupAgent.BackupAgentClient(_channel);
        await WaitReadyAsync();
    }

    public async Task StopAgentAsync()
    {
        _channel?.Dispose();
        _channel = null;
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            _app = null;
        }

        SqliteConnection.ClearAllPools();
    }

    public async Task RestartAsync()
    {
        await StopAgentAsync();
        await StartAgentAsync();
    }

    public async Task WaitReadyAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        while (!(await Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct)).Ready)
        {
            Assert.True(stopwatch.Elapsed < Timeout, "the agent never became ready");
            await Task.Delay(100, Ct);
        }
    }

    public Task<Destination> CreateDestinationAsync(string? location = null, string name = "Backup disk") =>
        Client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = name,
            Kind = DestinationKind.FileSystem,
            Location = location ?? Destination,
            Password = Password,
        }, cancellationToken: Ct).ResponseAsync;

    public static JobSpec Spec(string destinationId, string source, Schedule? schedule = null) => new()
    {
        Name = "Documents",
        DestinationId = destinationId,
        SourcePath = source,
        Compression = Compression.Balanced,
        Retention = new Retention { KeepLatest = 10, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, KeepAnnual = 2 },
        Schedule = schedule ?? new Schedule { Manual = new ManualSchedule() },
        Enabled = true,
    };

    public async Task<Job> CreateJobAsync(string destinationId, Schedule? schedule = null) =>
        await Client.CreateJobAsync(new CreateJobRequest { Spec = Spec(destinationId, Source, schedule) }, cancellationToken: Ct);

    /// <summary>Watches a run to its end; returns every update and the finished run.</summary>
    public async Task<(List<RunProgress> Updates, Contracts.V1.Run Finished)> WatchAsync(string runId)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);
        using var call = Client.WatchRun(new WatchRunRequest { RunId = runId }, cancellationToken: timeout.Token);
        var updates = new List<RunProgress>();
        await foreach (var update in call.ResponseStream.ReadAllAsync(timeout.Token))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var finished = updates[^1].Finished;
        Assert.NotNull(finished);
        Assert.All(updates.SkipLast(1), u => Assert.Null(u.Finished));
        return (updates, finished);
    }

    public async Task<Contracts.V1.Run> RunNowAsync(string jobId)
    {
        var run = await Client.RunJobNowAsync(new RunJobNowRequest { JobId = jobId }, cancellationToken: Ct);
        return (await WatchAsync(run.Id)).Finished;
    }

    /// <summary>Direct access to state.db while the agent is stopped (to simulate crashes and the passing of time).</summary>
    public async Task WithStateDbAsync(Func<StateDbContext, Task> action)
    {
        Assert.Null(_app);
        var options = new DbContextOptionsBuilder<StateDbContext>();
        options.UseStateDb(Path.Combine(Data, StateDb.FileName));
        await using (var db = new StateDbContext(options.Options))
        {
            await action(db);
        }

        SqliteConnection.ClearAllPools();
    }

    public static string ErrorKind(RpcException ex) => ex.Trailers.GetValue("cashgear-error-kind") ?? "";

    /// <summary>Every agent log so far (the running agent keeps the current file open).</summary>
    public string ReadLogs() => string.Concat(Directory.GetFiles(Path.Combine(Data, "logs")).Select(file =>
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }));

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_WORKSPACE")))
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(200);
                }
            }
        }
    }
}
