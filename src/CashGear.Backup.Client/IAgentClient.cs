using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Client;

/// <summary>
/// Everything the desktop app asks of the agent. Failures are <see cref="AgentCallException"/>s.
/// The Manage calls (destinations, job changes, restore) need an elevated administrator: when the app's own
/// connection may not make them (<see cref="AgentStatus.CallerCanManage"/> is false) they go through the
/// elevated helper, and Windows asks the user for approval first.
/// </summary>
public interface IAgentClient
{
    Task<AgentStatus> GetStatusAsync(CancellationToken ct);

    Task<IReadOnlyList<Destination>> ListDestinationsAsync(CancellationToken ct);

    /// <summary>Manage. Creates a new, encrypted repository; the request carries its password.</summary>
    Task<Destination> CreateDestinationAsync(CreateDestinationRequest request, CancellationToken ct);

    /// <summary>Manage. Adds an existing repository; the request carries its password.</summary>
    Task<Destination> ConnectDestinationAsync(ConnectDestinationRequest request, CancellationToken ct);

    Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct);

    /// <summary>Manage.</summary>
    Task<Job> CreateJobAsync(JobSpec spec, CancellationToken ct);

    /// <summary>Manage. The destination and source folder cannot change.</summary>
    Task<Job> UpdateJobAsync(string jobId, JobSpec spec, CancellationToken ct);

    /// <summary>Manage. Removes the job only; its backups stay on the destination.</summary>
    Task DeleteJobAsync(string jobId, CancellationToken ct);

    Task<Run> RunJobNowAsync(string jobId, CancellationToken ct);

    /// <summary>Newest first; <paramref name="jobId"/> null for every job.</summary>
    Task<IReadOnlyList<Run>> ListRunsAsync(string? jobId, int limit, CancellationToken ct);

    /// <summary>Progress until the run ends; the last update has <see cref="RunProgress.Finished"/> set.</summary>
    IAsyncEnumerable<RunProgress> WatchRunAsync(string runId, CancellationToken ct);

    Task CancelRunAsync(string runId, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string jobId, CancellationToken ct);

    Task<IReadOnlyList<SnapshotEntry>> BrowseSnapshotAsync(string jobId, string snapshotId, string path, CancellationToken ct);

    /// <summary>Name search in a snapshot; the last response has <see cref="SearchSnapshotResponse.Done"/> set.</summary>
    IAsyncEnumerable<SearchSnapshotResponse> SearchSnapshotAsync(string jobId, string snapshotId, string query, int maxResults, CancellationToken ct);

    /// <summary>Manage. Restores into a new or empty folder, or back into the job's folder keeping both versions; never overwrites.</summary>
    Task<Run> StartRestoreAsync(StartRestoreRequest request, CancellationToken ct);

    /// <summary>Checks the job's backups now; a run of kind Verify.</summary>
    Task<Run> VerifyJobNowAsync(string jobId, CancellationToken ct);

    /// <summary>Most serious first.</summary>
    Task<IReadOnlyList<Alert>> ListAlertsAsync(CancellationToken ct);

    /// <summary>The email password is never returned.</summary>
    Task<Settings> GetSettingsAsync(CancellationToken ct);

    /// <summary>Manage. Carries the email password when one is typed.</summary>
    Task<Settings> UpdateSettingsAsync(Settings settings, CancellationToken ct);

    /// <summary>Manage. An empty password uses the saved one.</summary>
    Task SendTestEmailAsync(EmailSettings email, CancellationToken ct);
}
