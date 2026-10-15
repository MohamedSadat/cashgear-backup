using System.Collections.Concurrent;
using System.Diagnostics;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Engine.Kopia.Policy;
using CashGear.Backup.Engine.Kopia.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Backup.Engine.Kopia;

/// <summary>
/// <see cref="IBackupEngine"/> on Kopia: one supervised <c>kopia server</c> per connected repository, driven
/// over its local REST API. Repositories are connected with <see cref="CreateRepositoryAsync"/> or
/// <see cref="ConnectRepositoryAsync"/> and stay connected until the engine is disposed.
/// </summary>
public sealed partial class KopiaBackupEngine : IBackupEngine
{
    private const string StubPage = "<!doctype html><html><head><title>CashGear</title></head><body></body></html>";

    private readonly KopiaEngineOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, RepositorySession> _sessions = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RunTracker> _runs = new();
    private readonly ConcurrentDictionary<string, Guid> _activeSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposing = new();
    private int _disposed;

    public KopiaBackupEngine(KopiaEngineOptions options, ILoggerFactory? loggerFactory = null)
    {
        BackupEngineException.ThrowIfInvalid(options.Validate(), "Kopia engine options");
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<KopiaBackupEngine>();
    }

    private string HtmlDirectory => Path.Combine(_options.DataDirectory, "html");

    public async Task<RepositoryInfo> CreateRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct)
    {
        ThrowIfDisposed();
        var storage = StorageFor(dest);
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessions.ContainsKey(dest.RepositoryId))
            {
                throw new BackupEngineException(BackupErrorKind.RepositoryAlreadyExists, $"Repository {dest.RepositoryId} is already connected.");
            }

            var layout = KopiaRepositoryLayout.For(_options.DataDirectory, dest.RepositoryId);
            if (File.Exists(layout.ConfigFile))
            {
                throw new BackupEngineException(BackupErrorKind.RepositoryAlreadyExists,
                    $"Repository {dest.RepositoryId} is already set up on this machine; connect to it instead.");
            }

            var host = NewHost(dest);
            try
            {
                await host.StartAsync(null, ct);
                var client = (await host.GetSessionAsync(ct)).Client;
                await client.CreateRepositoryAsync(RepositoryRequest(storage, password, create: true), ct);
                return await RegisterAsync(dest, host, client, password, "created", ct);
            }
            catch (Exception ex)
            {
                await AbandonHostAsync(host, layout);
                if (IsKopiaFailure(ex))
                {
                    throw Map(ex);
                }

                throw;
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<RepositoryInfo> ConnectRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct)
    {
        ThrowIfDisposed();
        var storage = StorageFor(dest);
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(dest.RepositoryId, out var existing))
            {
                try
                {
                    return Info(existing, await (await ClientAsync(existing, ct)).GetRepoStatusAsync(ct));
                }
                catch (Exception ex) when (IsKopiaFailure(ex))
                {
                    throw Map(ex);
                }
            }

            var layout = KopiaRepositoryLayout.For(_options.DataDirectory, dest.RepositoryId);
            var host = NewHost(dest);
            try
            {
                if (File.Exists(layout.ConfigFile))
                {
                    // Kopia opens the repository at start from the config and KOPIA_PASSWORD.
                    await host.StartAsync(password, ct);
                    return await RegisterAsync(dest, host, (await host.GetSessionAsync(ct)).Client, password, "opened", ct);
                }

                await host.StartAsync(null, ct);
                var client = (await host.GetSessionAsync(ct)).Client;
                await client.ConnectRepositoryAsync(RepositoryRequest(storage, password, create: false), ct);
                return await RegisterAsync(dest, host, client, password, "connected", ct);
            }
            catch (Exception ex)
            {
                await AbandonHostAsync(host, layout);
                if (IsKopiaFailure(ex))
                {
                    throw Map(ex);
                }

                throw;
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task ApplyJobPolicyAsync(BackupJob job, CancellationToken ct)
    {
        ThrowIfDisposed();
        BackupEngineException.ThrowIfInvalid(job.Validate(), "Backup job");
        var session = Session(job.RepositoryId);
        try
        {
            var client = await ClientAsync(session, ct);
            await WritePolicyAsync(session, client, job, session.Source(NormalizeSourcePath(job.SourcePath)), ct);
        }
        catch (Exception ex) when (IsKopiaFailure(ex))
        {
            throw Map(ex);
        }
    }

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(BackupJob job, CancellationToken ct)
    {
        ThrowIfDisposed();
        BackupEngineException.ThrowIfInvalid(job.Validate(), "Backup job");
        var session = Session(job.RepositoryId);
        var source = session.Source(NormalizeSourcePath(job.SourcePath));
        try
        {
            var client = await ClientAsync(session, ct);
            return [.. (await client.ListSnapshotsAsync(source, ct)).Snapshots
                .Select(s => ToSnapshotInfo(session, source, s))
                .OrderByDescending(s => s.StartTime)];
        }
        catch (Exception ex) when (IsKopiaFailure(ex))
        {
            throw Map(ex);
        }
    }

    public async Task<IReadOnlyList<SnapshotEntry>> BrowseAsync(SnapshotInfo snap, string path, CancellationToken ct)
    {
        ThrowIfDisposed();
        var session = Session(snap.RepositoryId);
        var segments = SnapshotPath.Split(path) ?? throw InvalidPath(path);
        try
        {
            var client = await ClientAsync(session, ct);
            var directory = await client.GetDirectoryAsync(ValidRootId(snap), ct);
            foreach (var segment in segments)
            {
                var entry = directory.Entries.FirstOrDefault(e => e.Name == segment);
                if (entry is not { Type: DirectoryEntry.TypeDirectory } || !KopiaApiClient.IsValidObjectId(entry.ObjectId))
                {
                    throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"'{path}' is not a folder in snapshot {snap.Id}.");
                }

                directory = await client.GetDirectoryAsync(entry.ObjectId!, ct);
            }

            return [.. directory.Entries.Select(e => new SnapshotEntry(
                e.Name,
                string.Join('/', segments.Append(e.Name)),
                e.Type switch
                {
                    DirectoryEntry.TypeDirectory => SnapshotEntryKind.Directory,
                    DirectoryEntry.TypeFile => SnapshotEntryKind.File,
                    DirectoryEntry.TypeSymlink => SnapshotEntryKind.Symlink,
                    _ => SnapshotEntryKind.Other,
                },
                e.Type == DirectoryEntry.TypeDirectory ? e.Summary?.Size ?? 0 : e.Size,
                e.Mtime))];
        }
        catch (Exception ex) when (IsKopiaFailure(ex))
        {
            throw Map(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _disposing.CancelAsync();
        try
        {
            await Task.WhenAll(_runs.Values.Select(r => r.Monitor)).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            // Monitors end on their own once their next call fails; the servers stop below either way.
        }

        await Task.WhenAll(_sessions.Values.Select(s => s.Host.DisposeAsync().AsTask()));
        _sessions.Clear();
        _disposing.Dispose();
    }

    /// <summary>Test support: the server host of a connected repository.</summary>
    internal KopiaProcessHost? HostFor(Guid repositoryId) => _sessions.TryGetValue(repositoryId, out var session) ? session.Host : null;

    /// <summary>Test support: the Kopia source a job is recorded under.</summary>
    internal SourceInfo SourceFor(BackupJob job) => Session(job.RepositoryId).Source(NormalizeSourcePath(job.SourcePath));

    private async Task<RepositoryInfo> RegisterAsync(DestinationSpec dest, KopiaProcessHost host, KopiaApiClient client, SecureSecret password, string how, CancellationToken ct)
    {
        // repo/create and repo/connect answer with the status from before connecting (FINDINGS #1).
        var status = await WaitForConnectedAsync(client, ct);
        host.SetRepositoryPassword(password);
        var session = new RepositorySession(dest, host,
            status.Username ?? throw Protocol("repo/status has no username"),
            status.Hostname ?? throw Protocol("repo/status has no hostname"));
        _sessions[dest.RepositoryId] = session;
        LogRepositoryReady(_logger, dest.RepositoryId, how, dest.DisplayLocation, session.Identity);
        return Info(session, status);
    }

    private async Task<RepoStatus> WaitForConnectedAsync(KopiaApiClient client, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var status = await client.GetRepoStatusAsync(ct);
            if (status.Connected)
            {
                return status;
            }

            if (stopwatch.Elapsed > _options.ConnectTimeout)
            {
                throw new BackupEngineException(BackupErrorKind.EngineUnavailable,
                    $"The repository did not report itself connected within {_options.ConnectTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(100, ct);
        }
    }

    private async Task WritePolicyAsync(RepositorySession session, KopiaApiClient client, BackupJob job, SourceInfo source, CancellationToken ct)
    {
        var policy = KopiaPolicyMapper.ToPolicy(job);
        await EnsureCompressorAsync(session, client, policy.Compression.CompressorName, ct);

        if ((await client.ListSourcesAsync(ct)).Sources.Any(s => s.Source == source))
        {
            await client.SetPolicyAsync(source, policy, ct);
        }
        else
        {
            await client.CreateSourceAsync(new CreateSourceRequest(source.Path, CreateSnapshot: false, policy), ct);
        }

        // Kopia drops fields it does not know without an error: read the policy back.
        var differences = KopiaPolicyMapper.Differences(policy, await client.GetPolicyAsync(source, ct));
        if (differences.Count > 0)
        {
            throw Protocol($"Kopia did not store the job policy as written: {string.Join("; ", differences)}");
        }

        LogPolicyWritten(_logger, job.Id, source.Path);
    }

    private static async Task EnsureCompressorAsync(RepositorySession session, KopiaApiClient client, string name, CancellationToken ct)
    {
        if (name == KopiaPolicyMapper.NoCompression)
        {
            return;
        }

        // Kopia accepts any compressor name in a policy and only fails when a backup runs.
        session.Compressors ??= [.. (await client.GetAlgorithmsAsync(ct)).Compression.Where(a => !a.Deprecated).Select(a => a.Id)];
        if (!session.Compressors.Contains(name))
        {
            throw Protocol($"This Kopia build does not offer the '{name}' compressor.");
        }
    }

    private RepositorySession Session(Guid repositoryId)
    {
        ThrowIfDisposed();
        return _sessions.TryGetValue(repositoryId, out var session)
            ? session
            : throw new BackupEngineException(BackupErrorKind.RepositoryNotConnected,
                $"Repository {repositoryId} is not connected; call ConnectRepositoryAsync first.");
    }

    private static async Task<KopiaApiClient> ClientAsync(RepositorySession session, CancellationToken ct) =>
        (await session.Host.GetSessionAsync(ct)).Client;

    private KopiaProcessHost NewHost(DestinationSpec dest)
    {
        var repositoryId = dest.RepositoryId;
        Directory.CreateDirectory(HtmlDirectory);
        var page = Path.Combine(HtmlDirectory, "index.html");
        if (!File.Exists(page))
        {
            // Kopia serves this instead of its web UI; GET / is only used to obtain the CSRF token.
            File.WriteAllText(page, StubPage);
        }

        return new KopiaProcessHost(new KopiaProcessHostOptions
        {
            KopiaExePath = _options.KopiaExePath,
            Layout = KopiaRepositoryLayout.For(_options.DataDirectory, repositoryId),
            HtmlDirectory = HtmlDirectory,
            Name = repositoryId.ToString("D"),
            StartTimeout = _options.ServerStartTimeout,
            HealthCheckInterval = _options.HealthCheckInterval,
            RestartBackoffInitial = _options.RestartBackoffInitial,
            RestartBackoffMax = _options.RestartBackoffMax,
            StorageEnvironment = StorageEnvironmentFor(dest),
        }, _loggerFactory);
    }

    /// <summary>Stops a host that never became a session and removes its folder if Kopia wrote no config. Never touches the destination.</summary>
    private static async Task AbandonHostAsync(KopiaProcessHost host, KopiaRepositoryLayout layout)
    {
        await host.DisposeAsync();
        if (!File.Exists(layout.ConfigFile))
        {
            try
            {
                Directory.Delete(layout.Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kopia's log files can stay locked for a moment; the folder is reused on the next attempt.
            }
        }
    }

    private RepositoryRequest RepositoryRequest(StorageConnection storage, SecureSecret password, bool create) =>
        new(storage, password.Reveal(), new ClientOptions(_options.ClientHostName, _options.ClientUserName, "CashGear Backup"))
        {
            Options = create ? [] : null,
        };

    private static StorageConnection StorageFor(DestinationSpec dest)
    {
        BackupEngineException.ThrowIfInvalid(dest.Validate(), "Destination");
        return dest switch
        {
            FileSystemDestination fs => new StorageConnection("filesystem", new() { ["path"] = Path.GetFullPath(fs.Path) }),
            SftpDestination sftp => new StorageConnection("sftp", SftpConfig(sftp)),
            // Keys deliberately empty: Kopia would write them into repository.config in clear. It reads them from the
            // server's environment instead (StorageEnvironmentFor).
            S3Destination s3 => new StorageConnection("s3", new()
            {
                ["bucket"] = s3.Bucket,
                ["prefix"] = s3.Prefix,
                ["endpoint"] = s3.Endpoint,
                ["region"] = s3.Region ?? "",
                ["doNotUseTLS"] = !s3.UseTls,
                ["accessKeyID"] = "",
                ["secretAccessKey"] = "",
                ["sessionToken"] = "",
            }),
            _ => throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"Destination type {dest.GetType().Name} is not supported yet."),
        };
    }

    /// <summary>
    /// SFTP settings as Kopia stores them. Kopia has no other way to take the login, so the password or key ends up
    /// in repository.config inside the engine's data folder (SYSTEM and Administrators only).
    /// </summary>
    private static Dictionary<string, object> SftpConfig(SftpDestination sftp)
    {
        var config = new Dictionary<string, object>
        {
            ["host"] = sftp.Host,
            ["port"] = sftp.Port,
            ["username"] = sftp.Username,
            ["path"] = sftp.Path,
            ["knownHostsData"] = string.Join('\n', sftp.KnownHosts),
            ["externalSSH"] = false,
        };
        if (sftp.Password is { } password)
        {
            config["password"] = password.Reveal();
        }
        else if (sftp.PrivateKey is { } key)
        {
            config["keyData"] = key.Reveal();
        }

        return config;
    }

    /// <summary>Copies of the storage credentials Kopia takes from its environment; the host owns them.</summary>
    private static Dictionary<string, SecureSecret> StorageEnvironmentFor(DestinationSpec dest) => dest switch
    {
        S3Destination s3 => new()
        {
            ["AWS_ACCESS_KEY_ID"] = new SecureSecret(s3.AccessKeyId),
            ["AWS_SECRET_ACCESS_KEY"] = s3.SecretAccessKey.Copy(),
        },
        _ => [],
    };

    private static RepositoryInfo Info(RepositorySession session, RepoStatus status) => new()
    {
        RepositoryId = session.Destination.RepositoryId,
        Location = session.Destination.DisplayLocation,
        ClientIdentity = session.Identity,
        Encryption = status.Encryption,
        Hashing = status.Hash,
        SupportsCompression = status.SupportsContentCompression,
    };

    private static SnapshotInfo ToSnapshotInfo(RepositorySession session, SourceInfo source, SnapshotItem s) => new()
    {
        Id = s.Id,
        RepositoryId = session.Destination.RepositoryId,
        SourcePath = source.Path,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        RootId = s.RootId,
        Files = s.Summary?.Files ?? 0,
        Directories = s.Summary?.Dirs ?? 0,
        Bytes = s.Summary?.Size ?? 0,
        FailedItemCount = (s.Summary?.NumFailed ?? 0) + (s.Summary?.NumIgnoredErrors ?? 0),
        IncompleteReason = s.IncompleteReason,
    };

    /// <summary>Kopia keys sources by the exact path string: absolute, no trailing separator (except a drive root).</summary>
    private static string NormalizeSourcePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ValidRootId(SnapshotInfo snap) =>
        KopiaApiClient.IsValidObjectId(snap.RootId)
            ? snap.RootId
            : throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"Snapshot {snap.Id} has no valid root reference.");

    private static BackupEngineException InvalidPath(string? path) =>
        new(BackupErrorKind.InvalidRequest, $"'{path}' is not a valid path inside a snapshot.");

    private static BackupEngineException Protocol(string message) => new(BackupErrorKind.EngineProtocolError, message);

    private static bool IsKopiaFailure(Exception ex) =>
        ex is KopiaApiException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException };

    /// <summary>Kopia and transport failures (<see cref="IsKopiaFailure"/>) as <see cref="BackupEngineException"/>.</summary>
    private static Exception Map(Exception ex) => ex switch
    {
        KopiaApiException api => new BackupEngineException(api.Code switch
        {
            KopiaApiException.InvalidPassword => BackupErrorKind.InvalidPassword,
            KopiaApiException.NotInitialized => BackupErrorKind.RepositoryNotFound,
            KopiaApiException.AlreadyInitialized => BackupErrorKind.RepositoryAlreadyExists,
            KopiaApiException.StorageConnection => StorageFailure(api.Message),
            _ when IsStorageMessage(api.Message) => StorageFailure(api.Message),
            _ => BackupErrorKind.EngineProtocolError,
        }, api.Message, api),
        HttpRequestException http => new BackupEngineException(BackupErrorKind.EngineUnavailable, $"The backup engine did not answer: {http.Message}", http),
        TaskCanceledException { InnerException: TimeoutException } timeout =>
            new BackupEngineException(BackupErrorKind.EngineUnavailable, "The backup engine did not answer in time.", timeout),
        _ => ex,
    };

    /// <summary>What a storage error means for the user: a changed SFTP host key, refused credentials, or no connection.</summary>
    internal static BackupErrorKind StorageFailure(string message) =>
        message.Contains("knownhosts", StringComparison.OrdinalIgnoreCase) || message.Contains("host key", StringComparison.OrdinalIgnoreCase)
            ? BackupErrorKind.DestinationNotTrusted
            : s_accessDenied.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))
                ? BackupErrorKind.DestinationAccessDenied
                : BackupErrorKind.DestinationUnreachable;

    private static readonly string[] s_accessDenied =
    [
        "unable to authenticate", "permission denied",
        // S3: error codes, and the services' own wording, which Kopia passes on instead of the code.
        "Access Denied", "AccessDenied", "InvalidAccessKeyId", "SignatureDoesNotMatch",
        "signature we calculated does not match", "Access Key Id you provided does not exist",
    ];

    private static bool IsStorageMessage(string message) =>
        message.Contains("can't connect to storage", StringComparison.OrdinalIgnoreCase) || message.Contains("unable to open", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("SFTP", StringComparison.OrdinalIgnoreCase) || message.Contains("bucket", StringComparison.OrdinalIgnoreCase));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private sealed class RepositorySession(DestinationSpec destination, KopiaProcessHost host, string userName, string hostName)
    {
        public DestinationSpec Destination { get; } = destination;
        public KopiaProcessHost Host { get; } = host;
        public string Identity => $"{userName}@{hostName}";

        /// <summary>Compressors offered by this Kopia build, fetched once.</summary>
        public HashSet<string>? Compressors { get; set; }

        public SourceInfo Source(string path) => new(hostName, userName, path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Repository {RepositoryId} {How} at {Location} as {Identity}")]
    private static partial void LogRepositoryReady(ILogger logger, Guid repositoryId, string how, string location, string identity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Policy for job {JobId} written and verified for {Source}")]
    private static partial void LogPolicyWritten(ILogger logger, Guid jobId, string source);
}
