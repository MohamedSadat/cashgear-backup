namespace CashGear.Backup.Core;

/// <summary>
/// The backup engine as the rest of the product sees it. Implementations own their engine processes;
/// dispose the engine to stop them. Failures the caller should handle throw <see cref="BackupEngineException"/>.
/// </summary>
/// <remarks>
/// Guarantees every implementation must keep (CLAUDE.md rules 4 and 5):
/// <list type="bullet">
/// <item>A backup run is <see cref="RunState.Succeeded"/> only when it recorded exactly one new, complete
/// snapshot in which nothing failed or was skipped.</item>
/// <item>A restore never overwrites: the target must be a new or empty folder.</item>
/// <item>Every backup runs with the job's whole policy (retention, exclusions, compression) in force, never
/// the engine's defaults.</item>
/// </list>
/// </remarks>
public interface IBackupEngine : IAsyncDisposable
{
    /// <summary>Initializes a new, encrypted repository at the destination and connects to it.</summary>
    Task<RepositoryInfo> CreateRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct);

    /// <summary>
    /// Connects to an existing repository (first use on this machine, or after an agent restart). Calling it
    /// for a repository that is already connected returns its info. The engine keeps its own copy of
    /// <paramref name="password"/> for as long as the repository stays connected, so it can restart its
    /// process; the caller still owns and disposes the instance it passed.
    /// </summary>
    Task<RepositoryInfo> ConnectRepositoryAsync(DestinationSpec dest, SecureSecret password, CancellationToken ct);

    /// <summary>Writes the job's whole policy: retention, exclusions, compression, open-file handling, manual scheduling.</summary>
    Task ApplyJobPolicyAsync(BackupJob job, CancellationToken ct);

    /// <summary>
    /// Starts a backup of the job's folder and returns at once. The job's policy is checked, and rewritten if
    /// it drifted, before the run starts.
    /// </summary>
    Task<RunHandle> StartBackupAsync(BackupJob job, CancellationToken ct);

    /// <summary>
    /// Progress of a run until it finishes. The first item is the current state; the last has
    /// <see cref="RunProgress.Result"/> set. Works for runs that have already finished.
    /// </summary>
    IAsyncEnumerable<RunProgress> WatchAsync(RunHandle run, CancellationToken ct);

    /// <summary>Asks a run to stop. The run then finishes as Canceled (or as whatever it reached first).</summary>
    Task CancelAsync(RunHandle run, CancellationToken ct);

    /// <summary>Snapshots of the job's folder, newest first, including partial ones (see <see cref="SnapshotInfo.IsComplete"/>).</summary>
    Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(BackupJob job, CancellationToken ct);

    /// <summary>The entries of a folder in a snapshot; <paramref name="path"/> is relative to the snapshot root ("" for the root).</summary>
    Task<IReadOnlyList<SnapshotEntry>> BrowseAsync(SnapshotInfo snap, string path, CancellationToken ct);

    /// <summary>Starts restoring into a new or empty folder and returns at once. Never overwrites.</summary>
    Task<RunHandle> RestoreAsync(RestoreRequest request, CancellationToken ct);

    /// <summary>Checks repository consistency and reads back a sample of the job's data.</summary>
    Task<VerifyResult> VerifyAsync(BackupJob job, double sampleFraction, CancellationToken ct);

    /// <summary>Reclaims space after retention removed snapshots. <paramref name="full"/> also compacts data.</summary>
    Task RunMaintenanceAsync(RepositoryInfo repo, bool full, CancellationToken ct);
}
