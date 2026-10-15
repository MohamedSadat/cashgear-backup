using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Backup.Engine.Kopia;

/// <summary>
/// <see cref="IBackupEngine"/> on Kopia: one supervised <c>kopia server</c> per connected repository, driven
/// over its local REST API.
/// </summary>
public sealed partial class KopiaBackupEngine : IBackupEngine
{
    private readonly KopiaEngineOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public KopiaBackupEngine(KopiaEngineOptions options, ILoggerFactory? loggerFactory = null)
    {
        BackupEngineException.ThrowIfInvalid(options.Validate(), "Kopia engine options");
        _options = options;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public Task<RepositoryInfo> CreateRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct) => throw new NotImplementedException();

    public Task<RepositoryInfo> ConnectRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct) => throw new NotImplementedException();

    public Task ApplyJobPolicyAsync(BackupJob job, CancellationToken ct) => throw new NotImplementedException();

    public Task<RunHandle> StartBackupAsync(BackupJob job, CancellationToken ct) => throw new NotImplementedException();

    public IAsyncEnumerable<RunProgress> WatchAsync(RunHandle run, CancellationToken ct) => throw new NotImplementedException();

    public Task CancelAsync(RunHandle run, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(BackupJob job, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyList<SnapshotEntry>> BrowseAsync(SnapshotInfo snap, string path, CancellationToken ct) => throw new NotImplementedException();

    public Task<RunHandle> RestoreAsync(RestoreRequest request, CancellationToken ct) => throw new NotImplementedException();

    public Task<VerifyResult> VerifyAsync(BackupJob job, double sampleFraction, CancellationToken ct) => throw new NotImplementedException();

    public Task RunMaintenanceAsync(RepositoryInfo repo, bool full, CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();

    /// <summary>Test support: the server host of a connected repository.</summary>
    internal KopiaProcessHost? HostFor(Guid repositoryId) => throw new NotImplementedException();

    /// <summary>Test support: the Kopia source a job is recorded under.</summary>
    internal SourceInfo SourceFor(BackupJob job) => throw new NotImplementedException();
}
