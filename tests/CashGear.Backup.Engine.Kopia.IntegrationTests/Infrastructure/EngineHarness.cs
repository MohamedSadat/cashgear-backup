using System.Security.Cryptography;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

/// <summary>
/// A workspace, a captured log and a <see cref="KopiaBackupEngine"/> on the pinned kopia.exe, optionally with
/// a fresh filesystem repository already created. Everything goes through <see cref="IBackupEngine"/>.
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public const string ClientHost = "test-host";
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    private readonly List<KopiaBackupEngine> _engines = [];

    private EngineHarness(TestWorkspace workspace, TestLog log, string password)
    {
        Workspace = workspace;
        Log = log;
        PasswordText = password;
        Password = new SecureSecret(password);
        Destination = new FileSystemDestination(RepositoryId, workspace.Destination);
    }

    public TestWorkspace Workspace { get; }
    public TestLog Log { get; }

    /// <summary>The repository password as plain text, for leak scans. Never pass this to the engine.</summary>
    public string PasswordText { get; }

    public SecureSecret Password { get; }
    public Guid RepositoryId { get; } = Guid.NewGuid();
    public FileSystemDestination Destination { get; }
    public KopiaBackupEngine Engine { get; private set; } = null!;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<EngineHarness> CreateAsync(string name, bool createRepository = true, Func<KopiaEngineOptions, KopiaEngineOptions>? configure = null)
    {
        var harness = new EngineHarness(TestWorkspace.Create(name), new TestLog(), RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_", 28));
        try
        {
            harness.Engine = harness.NewEngine(configure: configure);
            if (createRepository)
            {
                await harness.Engine.CreateRepositoryAsync(harness.Destination, harness.Password, Ct);
            }

            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public KopiaEngineOptions Options(string? dataDirectory = null, string? exePath = null) => new()
    {
        KopiaExePath = exePath ?? KopiaTestBinary.ExePath,
        DataDirectory = dataDirectory ?? Workspace.Data,
        ClientHostName = ClientHost,
        PollInterval = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>Another engine (an agent restart, or a second machine with its own data folder); disposed with the harness.</summary>
    public KopiaBackupEngine NewEngine(string? dataDirectory = null, Func<KopiaEngineOptions, KopiaEngineOptions>? configure = null)
    {
        var options = Options(dataDirectory);
        var engine = new KopiaBackupEngine(configure is null ? options : configure(options), Log.Factory);
        _engines.Add(engine);
        return engine;
    }

    public void UseEngine(KopiaBackupEngine engine) => Engine = engine;

    public BackupJob Job(string? sourcePath = null) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = RepositoryId,
        Name = "test job",
        SourcePath = sourcePath ?? Workspace.Source,
    };

    /// <summary>The internal REST client of the engine's server for this repository (contract-level checks).</summary>
    public async Task<KopiaApiClient> ClientAsync()
    {
        var host = Engine.HostFor(RepositoryId) ?? throw new InvalidOperationException("Repository is not connected.");
        return (await host.GetSessionAsync(Ct)).Client;
    }

    public async Task<RunResult> BackupAsync(BackupJob job, KopiaBackupEngine? engine = null)
    {
        engine ??= Engine;
        var run = await engine.StartBackupAsync(job, Ct);
        return (await RunToEndAsync(run, engine: engine)).Result!;
    }

    public async Task<RunResult> RestoreAsync(SnapshotInfo snapshot, string target, string pathInSnapshot = "")
    {
        var run = await Engine.RestoreAsync(new RestoreRequest { Snapshot = snapshot, TargetFolder = target, PathInSnapshot = pathInSnapshot }, Ct);
        return (await RunToEndAsync(run)).Result!;
    }

    /// <summary>Watches a run to its end and returns the final update; every update seen goes into <paramref name="seen"/>.</summary>
    public async Task<RunProgress> RunToEndAsync(RunHandle run, List<RunProgress>? seen = null, KopiaBackupEngine? engine = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(RunTimeout);
        RunProgress? last = null;
        await foreach (var progress in (engine ?? Engine).WatchAsync(run, timeout.Token))
        {
            seen?.Add(progress);
            last = progress;
        }

        Assert.NotNull(last);
        Assert.NotNull(last.Result);
        Assert.Equal(last.State, last.Result.State);
        return last;
    }

    public static void AssertIdentical(TreeManifest expected, string restoredPath)
    {
        Assert.True(Directory.Exists(restoredPath), $"restore target {restoredPath} does not exist");
        var differences = expected.ContentDifferences(TreeManifest.Capture(restoredPath));
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(20)));
    }

    /// <summary>Kopia's REST default restores <c>.kopia-entry</c> placeholders instead of data (CLAUDE.md rule 4).</summary>
    public static void AssertNoPlaceholders(string root)
    {
        var placeholders = Directory.GetFiles(root, "*.kopia-entry", SearchOption.AllDirectories);
        Assert.True(placeholders.Length == 0, "placeholder files restored: " + string.Join(", ", placeholders.Take(5)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var engine in _engines)
            {
                await engine.DisposeAsync();
            }
        }
        finally
        {
            Password.Dispose();
            Log.Dispose();
            Workspace.Dispose();
        }
    }
}
