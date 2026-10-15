using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Runs;

/// <summary>One update of <see cref="RunCoordinator.WatchAsync"/>: progress while running, then the finished record.</summary>
internal sealed record RunUpdate(Guid RunId, RunProgress? Progress, RunRecord? Finished);

/// <summary>
/// Starts backups and restores, records every run in state.db, follows it to its end and stores the
/// result. A backup that cannot start for an operational reason (destination unplugged, source missing)
/// is still recorded, as a failed run, so the history shows it.
/// </summary>
internal sealed partial class RunCoordinator(IDbContextFactory<StateDbContext> dbs, EngineHost engines, TimeProvider time, ILogger<RunCoordinator> logger)
{
    public const string InterruptedMessage = "The agent stopped during this run (shutdown, crash or power loss).";

    /// <summary>Restores into a job's own folder stage here first, inside that folder (same volume, so moves are renames).</summary>
    public const string StagingPrefix = ".cashgear-restore-";

    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();

    /// <summary>Jobs whose folder is in use: a backup, or a restore into the job's own folder.</summary>
    private readonly ConcurrentDictionary<Guid, Guid> _activeJobs = new();

    private readonly ConcurrentDictionary<Guid, Guid> _activeVerifies = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>A backup of the job, or a restore into its folder, is running.</summary>
    public bool IsBackupRunning(Guid jobId) => _activeJobs.ContainsKey(jobId);

    public bool IsVerifying(Guid jobId) => _activeVerifies.ContainsKey(jobId);

    /// <summary>Marks runs left "running" by a previous agent process as failed.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        foreach (var run in await db.Runs.Where(r => r.State == RunState.Running).ToListAsync(ct))
        {
            run.State = RunState.Failed;
            run.FinishedAt = now;
            run.Message = InterruptedMessage;
            LogInterrupted(logger, run.Id, run.JobId);
            if (run.Kind == RunKind.Restore && run.JobId is { } jobId && await db.Jobs.FindAsync([jobId], ct) is { } job)
            {
                // A restore into the job's folder stopped half way: what it staged is only restored data (still in
                // the snapshot), and nothing was merged yet or the merge was cut short. Drop the staging folder so
                // the next backup does not pick it up.
                DeleteStaging(Path.Combine(job.SourcePath, StagingPrefix + run.Id.ToString("N")));
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<RunRecord> StartBackupAsync(Guid jobId, RunTrigger trigger, CancellationToken ct)
    {
        JobRecord job;
        DestinationRecord destination;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            job = await db.Jobs.FindAsync([jobId], ct) ?? throw new AgentException(AgentError.NotFound, $"Job {jobId} does not exist.");
            destination = await db.Destinations.FindAsync([job.DestinationId], ct)
                ?? throw new AgentException(AgentError.NotFound, $"The destination of job {jobId} does not exist.");
        }

        var run = new RunRecord { Id = Guid.NewGuid(), JobId = jobId, Kind = RunKind.Backup, State = RunState.Running, Trigger = trigger, StartedAt = time.GetUtcNow() };
        if (!_activeJobs.TryAdd(jobId, run.Id))
        {
            throw new AgentException(AgentError.FailedPrecondition, "A backup of this job, or a restore into its folder, is already running.");
        }

        try
        {
            await InsertAsync(run, ct);
            if (!EngineHost.IsReachable(destination))
            {
                return await FinishEarlyAsync(run, $"The destination {destination.Location} is not reachable (disconnected drive or offline share).");
            }

            var engine = await engines.ConnectAsync(destination, ct);
            var handle = await engine.StartBackupAsync(JobMapping.ToEngineJob(job), ct);
            Follow(run, handle, engine);
            LogStarted(logger, run.Id, RunKind.Backup, jobId, trigger);
            return run;
        }
        catch (BackupEngineException ex)
        {
            return await FinishEarlyAsync(run, ex.Message);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, release the job and leave no run marked as running.
            await FinishEarlyAsync(run, "The backup could not start: " + ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Starts a restore. Unlike backups, a restore the engine refuses (for example a non-empty target) is not recorded.
    /// With <paramref name="toOriginal"/> the files go back into the job's folder without replacing anything there
    /// (see <see cref="KeepBothMerge"/>); <paramref name="targetFolder"/> is then ignored.
    /// </summary>
    public async Task<RunRecord> StartRestoreAsync(Guid jobId, string snapshotId, string pathInSnapshot, string targetFolder, bool toOriginal, CancellationToken ct)
    {
        var (job, destination) = await LoadJobAsync(jobId, ct);
        var engine = await engines.ConnectAsync(destination, ct);
        var engineJob = JobMapping.ToEngineJob(job);
        var snapshot = (await engine.ListSnapshotsAsync(engineJob, ct)).FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new AgentException(AgentError.NotFound, $"Snapshot {snapshotId} is not a snapshot of this job.");
        if (toOriginal)
        {
            return await StartRestoreToOriginalAsync(job, engine, snapshot, pathInSnapshot, ct);
        }

        var handle = await engine.RestoreAsync(new RestoreRequest { Snapshot = snapshot, PathInSnapshot = pathInSnapshot, TargetFolder = targetFolder }, ct);

        var run = new RunRecord
        {
            Id = Guid.NewGuid(), JobId = jobId, Kind = RunKind.Restore, State = RunState.Running, Trigger = RunTrigger.Manual,
            StartedAt = time.GetUtcNow(), SnapshotId = snapshot.Id, RestoreTarget = Path.GetFullPath(targetFolder),
        };
        await InsertAsync(run, CancellationToken.None);
        Follow(run, handle, engine);
        LogStarted(logger, run.Id, RunKind.Restore, jobId, RunTrigger.Manual);
        return run;
    }

    /// <summary>
    /// Restores into the job's own folder. The engine restores into a new, empty staging folder inside it (CLAUDE.md
    /// rule 4 holds for the engine call); once that finished, <see cref="KeepBothMerge"/> moves the files into place
    /// without replacing anything. The job counts as busy meanwhile, so no backup picks up a half-merged folder.
    /// </summary>
    private async Task<RunRecord> StartRestoreToOriginalAsync(JobRecord job, IBackupEngine engine, SnapshotInfo snapshot, string pathInSnapshot, CancellationToken ct)
    {
        var segments = SnapshotPath.Split(pathInSnapshot)
            ?? throw new AgentException(AgentError.InvalidArgument, $"'{pathInSnapshot}' is not a path inside the snapshot.");
        var isFile = false;
        if (segments.Count > 0)
        {
            var entry = (await engine.BrowseAsync(snapshot, string.Join('/', segments.SkipLast(1)), ct)).FirstOrDefault(e => e.Name == segments[^1])
                ?? throw new AgentException(AgentError.NotFound, $"{pathInSnapshot} is not in this snapshot.");
            isFile = entry.Kind != SnapshotEntryKind.Directory;
        }

        // Where the item lived: a folder's contents merge into that folder, a file into its parent folder.
        var mergeInto = Path.Combine([job.SourcePath, .. isFile ? segments.SkipLast(1) : segments]);
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), JobId = job.Id, Kind = RunKind.Restore, State = RunState.Running, Trigger = RunTrigger.Manual,
            StartedAt = time.GetUtcNow(), SnapshotId = snapshot.Id,
            RestoreTarget = isFile ? Path.Combine(mergeInto, segments[^1]) : mergeInto,
        };
        if (!_activeJobs.TryAdd(job.Id, run.Id))
        {
            throw new AgentException(AgentError.FailedPrecondition, "A backup of this job is running; restore into its folder once it has finished.");
        }

        var staging = Path.Combine(job.SourcePath, StagingPrefix + run.Id.ToString("N"));
        try
        {
            try
            {
                Directory.CreateDirectory(staging);
                File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AgentException(AgentError.FailedPrecondition, $"The job's folder {job.SourcePath} cannot be written to: {ex.Message}");
            }

            var handle = await engine.RestoreAsync(new RestoreRequest { Snapshot = snapshot, PathInSnapshot = pathInSnapshot, TargetFolder = staging }, ct);
            await InsertAsync(run, CancellationToken.None);
            var stamp = time.GetLocalNow().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
            Follow(run, handle, engine, busyJobId: job.Id, afterEngine: result => Task.Run(() => FinishRestoreToOriginal(result, staging, mergeInto, stamp)));
            LogStarted(logger, run.Id, RunKind.Restore, job.Id, RunTrigger.Manual);
            return run;
        }
        catch
        {
            _activeJobs.TryRemove(KeyValuePair.Create(job.Id, run.Id));
            DeleteStaging(staging);
            throw;
        }
    }

    private static RunResult FinishRestoreToOriginal(RunResult result, string staging, string mergeInto, string stamp)
    {
        if (result.State is RunState.Failed or RunState.Canceled)
        {
            DeleteStaging(staging);
            return result with { Message = $"{result.Message} Nothing in the job's folder was changed.".Trim() };
        }

        var merge = KeepBothMerge.Merge(staging, mergeInto, stamp);
        var message = merge.Describe(mergeInto, stamp);
        if (Directory.Exists(staging))
        {
            // Some items could not be put in place: leave them where the user can find them.
            File.SetAttributes(staging, File.GetAttributes(staging) & ~FileAttributes.Hidden);
            message += $" They are still in {staging}.";
        }

        return result with
        {
            State = result.State == RunState.CompletedWithWarnings || merge.Failed.Count > 0 ? RunState.CompletedWithWarnings : RunState.Succeeded,
            Files = merge.Moved + merge.KeptBoth + merge.Identical,
            FailedItemCount = result.FailedItemCount + merge.Failed.Count,
            FailedItems = [.. result.FailedItems, .. merge.Failed],
            Message = message,
        };
    }

    private static void DeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start's cleanup; it only holds restored copies.
        }
    }

    /// <summary>
    /// Checks a job's backups (<see cref="IBackupEngine.VerifyAsync"/>) as a recorded run of kind Verify. One check per
    /// job at a time; it can run beside a backup.
    /// </summary>
    public async Task<RunRecord> StartVerifyAsync(Guid jobId, RunTrigger trigger, double sampleFraction, CancellationToken ct)
    {
        var (job, destination) = await LoadJobAsync(jobId, ct);
        var run = new RunRecord { Id = Guid.NewGuid(), JobId = jobId, Kind = RunKind.Verify, State = RunState.Running, Trigger = trigger, StartedAt = time.GetUtcNow() };
        if (!_activeVerifies.TryAdd(jobId, run.Id))
        {
            throw new AgentException(AgentError.FailedPrecondition, "This job's backups are being checked already.");
        }

        try
        {
            await InsertAsync(run, ct);
        }
        catch
        {
            _activeVerifies.TryRemove(KeyValuePair.Create(jobId, run.Id));
            throw;
        }

        var handle = new RunHandle(run.Id, RunKind.Verify, destination.Id, jobId, run.StartedAt);
        var active = new ActiveRun(run.Id, null, null, null, handle);
        _active[run.Id] = active;
        active.Follower = Task.Run(() => FollowVerifyAsync(active, job, destination, sampleFraction), CancellationToken.None);
        LogStarted(logger, run.Id, RunKind.Verify, jobId, trigger);
        return run;
    }

    private async Task FollowVerifyAsync(ActiveRun active, JobRecord job, DestinationRecord destination, double sampleFraction)
    {
        var started = active.Placeholder.StartedAt;
        RunResult? result;
        try
        {
            if (!EngineHost.IsReachable(destination))
            {
                result = Failed($"The destination {destination.Location} is not reachable (disconnected drive or offline share).");
            }
            else
            {
                var engine = await engines.ConnectAsync(destination, _stopping.Token);
                var verify = await engine.VerifyAsync(JobMapping.ToEngineJob(job), sampleFraction, _stopping.Token);
                result = new RunResult
                {
                    State = verify.Passed ? RunState.Succeeded : RunState.Failed,
                    StartedAt = started,
                    FinishedAt = time.GetUtcNow(),
                    Files = verify.FilesRead,
                    Bytes = verify.BytesRead,
                    FailedItemCount = verify.Errors.Count,
                    FailedItems = [.. verify.Errors.Select(e => new FailedItem("", e))],
                    Message = verify.Passed
                        ? $"No problems found: checked {verify.ItemsChecked:N0} items and read back {verify.FilesRead:N0} files."
                        : $"The check found {verify.Errors.Count} problem(s) with this job's backups at {destination.Location}.",
                };
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            result = null;
        }
        catch (BackupEngineException ex)
        {
            result = Failed("The check could not run: " + ex.Message);
        }
        catch (Exception ex)
        {
            LogFollowFailed(logger, ex, active.RunId);
            result = Failed("The check could not run; details are in the agent's log.");
        }

        await SaveAsync(active, result);
        _activeVerifies.TryRemove(KeyValuePair.Create(job.Id, active.RunId));

        RunResult Failed(string message) => new() { State = RunState.Failed, StartedAt = started, FinishedAt = time.GetUtcNow(), Message = message };
    }


    public async Task<(JobRecord Job, DestinationRecord Destination)> LoadJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var job = await db.Jobs.FindAsync([jobId], ct) ?? throw new AgentException(AgentError.NotFound, $"Job {jobId} does not exist.");
        var destination = await db.Destinations.FindAsync([job.DestinationId], ct)
            ?? throw new AgentException(AgentError.NotFound, $"The destination of job {jobId} does not exist.");
        return (job, destination);
    }

    public async Task CancelAsync(Guid runId, CancellationToken ct)
    {
        if (_active.TryGetValue(runId, out var active))
        {
            if (active.Engine is null || active.Handle is null)
            {
                throw new AgentException(AgentError.FailedPrecondition, "A check of backups cannot be stopped; it ends on its own.");
            }

            await active.Engine.CancelAsync(active.Handle, ct);
            return;
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.Runs.FindAsync([runId], ct) is null)
        {
            throw new AgentException(AgentError.NotFound, $"Run {runId} does not exist.");
        }
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(Guid? jobId, int limit, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var query = db.Runs.AsNoTracking();
        if (jobId is { } id)
        {
            query = query.Where(r => r.JobId == id);
        }

        return await query.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit <= 0 ? 50 : limit, 1, 500)).ToListAsync(ct);
    }

    /// <summary>Progress until the run ends, then the stored record. A finished run yields only the record.</summary>
    public async IAsyncEnumerable<RunUpdate> WatchAsync(Guid runId, [EnumeratorCancellation] CancellationToken ct)
    {
        // A run is in state.db before it is followed; wait for one or the other.
        for (var attempt = 0; ; attempt++)
        {
            if (_active.TryGetValue(runId, out var active))
            {
                RunProgress? last = null;
                if (active.Engine is null || active.Handle is null)
                {
                    // Runs the agent does itself (checks) report no progress beyond "running".
                    last = new RunProgress { Run = active.Placeholder, State = RunState.Running };
                    yield return new RunUpdate(runId, last, null);
                }
                else
                {
                    await foreach (var progress in active.Engine.WatchAsync(active.Handle, ct))
                    {
                        last = progress;
                        if (progress.Result is null)
                        {
                            yield return new RunUpdate(runId, progress, null);
                        }
                    }
                }

                await active.Saved.Task.WaitAsync(ct);
                yield return new RunUpdate(runId, last, await LoadAsync(runId, ct));
                yield break;
            }

            var record = await LoadAsync(runId, ct);
            if (record.State != RunState.Running || attempt >= 100)
            {
                yield return new RunUpdate(runId, null, record);
                yield break;
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>Stops following runs (the engine is stopped next); unfinished runs are recorded as interrupted.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_active.Values.Select(a => a.Follower)).WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <param name="busyJobId">The job released when the run ends (backups, and restores into the job's folder).</param>
    /// <param name="afterEngine">Agent-side work after the engine finished (the merge of a restore into the job's folder).</param>
    private void Follow(RunRecord run, RunHandle handle, IBackupEngine engine, Guid? busyJobId = null, Func<RunResult, Task<RunResult>>? afterEngine = null)
    {
        var active = new ActiveRun(run.Id, busyJobId ?? (run.Kind == RunKind.Backup ? run.JobId : null), handle, engine, handle) { AfterEngine = afterEngine };
        _active[run.Id] = active;
        active.Follower = Task.Run(() => FollowAsync(active), CancellationToken.None);
    }

    private async Task FollowAsync(ActiveRun active)
    {
        RunResult? result = null;
        try
        {
            await foreach (var progress in active.Engine!.WatchAsync(active.Handle!, _stopping.Token))
            {
                result = progress.Result ?? result;
            }

            if (result is not null && active.AfterEngine is { } after)
            {
                result = await after(result);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Agent shutdown: recorded as interrupted below.
        }
        catch (Exception ex)
        {
            LogFollowFailed(logger, ex, active.RunId);
        }

        await SaveAsync(active, result);
    }

    /// <summary>Stores the result (null: the agent stopped) and releases the run.</summary>
    private async Task SaveAsync(ActiveRun active, RunResult? result)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            var run = await db.Runs.FindAsync(active.RunId);
            if (run is not null)
            {
                Apply(run, result);
                await db.SaveChangesAsync(CancellationToken.None);
                LogFinished(logger, run.Id, run.Kind, run.State, run.SnapshotId, run.Files, run.FailedItemCount, run.Message);
            }
        }
        catch (Exception ex)
        {
            LogFollowFailed(logger, ex, active.RunId);
        }
        finally
        {
            _active.TryRemove(active.RunId, out _);
            if (active.BackupJobId is { } jobId)
            {
                _activeJobs.TryRemove(KeyValuePair.Create(jobId, active.RunId));
            }

            active.Saved.TrySetResult();
        }
    }

    private void Apply(RunRecord run, RunResult? result)
    {
        if (result is null)
        {
            run.State = RunState.Failed;
            run.FinishedAt = time.GetUtcNow();
            run.Message = InterruptedMessage;
            return;
        }

        run.State = result.State;
        run.FinishedAt = result.FinishedAt;
        run.Files = result.Files;
        run.Bytes = result.Bytes;
        run.FailedItemCount = result.FailedItemCount;
        run.FailedItems = [.. result.FailedItems.Take(StateDb.MaxStoredFailedItems).Select(f => new FailedItemRecord { Path = f.Path, Error = f.Error })];
        run.Message = result.Message;
        if (run.Kind == RunKind.Backup)
        {
            run.SnapshotId = result.SnapshotId;
        }
    }

    private async Task InsertAsync(RunRecord run, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
    }

    private async Task<RunRecord> FinishEarlyAsync(RunRecord run, string message)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            var stored = await db.Runs.FindAsync(run.Id);
            if (stored is not null)
            {
                stored.State = RunState.Failed;
                stored.FinishedAt = time.GetUtcNow();
                stored.Message = message;
                await db.SaveChangesAsync(CancellationToken.None);
                run = stored;
            }

            LogFinished(logger, run.Id, run.Kind, RunState.Failed, null, 0, 0, message);
            return run;
        }
        finally
        {
            if (run.JobId is { } jobId)
            {
                _activeJobs.TryRemove(KeyValuePair.Create(jobId, run.Id));
            }
        }
    }

    private async Task<RunRecord> LoadAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new AgentException(AgentError.NotFound, $"Run {runId} does not exist.");
    }

    /// <param name="busyJobId">The job this run keeps busy (released when it ends).</param>
    /// <param name="handle">The engine run; null for runs the agent does itself (checks).</param>
    /// <param name="placeholder">Identifies the run in progress updates.</param>
    private sealed class ActiveRun(Guid runId, Guid? busyJobId, RunHandle? handle, IBackupEngine? engine, RunHandle placeholder)
    {
        public Guid RunId { get; } = runId;
        public Guid? BackupJobId { get; } = busyJobId;
        public RunHandle? Handle { get; } = handle;
        public IBackupEngine? Engine { get; } = engine;
        public RunHandle Placeholder { get; } = placeholder;
        public Func<RunResult, Task<RunResult>>? AfterEngine { get; init; }
        public Task Follower { get; set; } = Task.CompletedTask;

        /// <summary>Completes once the result is in state.db.</summary>
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} run {RunId} of job {JobId} started ({Trigger})")]
    private static partial void LogStarted(ILogger logger, Guid runId, RunKind kind, Guid jobId, RunTrigger trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} run {RunId} finished: {State}, snapshot {SnapshotId}, {Files} files, {FailedItems} failed. {Message}")]
    private static partial void LogFinished(ILogger logger, Guid runId, RunKind kind, RunState state, string? snapshotId, long files, long failedItems, string? message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} of job {JobId} was cut off by an earlier agent stop; recorded as failed")]
    private static partial void LogInterrupted(ILogger logger, Guid runId, Guid? jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Following run {RunId} failed")]
    private static partial void LogFollowFailed(ILogger logger, Exception exception, Guid runId);
}
