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

    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeJobs = new();
    private readonly CancellationTokenSource _stopping = new();

    public bool IsBackupRunning(Guid jobId) => _activeJobs.ContainsKey(jobId);

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
            throw new AgentException(AgentError.FailedPrecondition, "A backup of this job is already running.");
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

    /// <summary>Starts a restore. Unlike backups, a restore the engine refuses (for example a non-empty target) is not recorded.</summary>
    public async Task<RunRecord> StartRestoreAsync(Guid jobId, string snapshotId, string pathInSnapshot, string targetFolder, CancellationToken ct)
    {
        var (job, destination) = await LoadJobAsync(jobId, ct);
        var engine = await engines.ConnectAsync(destination, ct);
        var engineJob = JobMapping.ToEngineJob(job);
        var snapshot = (await engine.ListSnapshotsAsync(engineJob, ct)).FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new AgentException(AgentError.NotFound, $"Snapshot {snapshotId} is not a snapshot of this job.");
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
                await foreach (var progress in active.Engine.WatchAsync(active.Handle, ct))
                {
                    last = progress;
                    if (progress.Result is null)
                    {
                        yield return new RunUpdate(runId, progress, null);
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

    private void Follow(RunRecord run, RunHandle handle, IBackupEngine engine)
    {
        var active = new ActiveRun(run.Id, run.Kind == RunKind.Backup ? run.JobId : null, handle, engine);
        _active[run.Id] = active;
        active.Follower = Task.Run(() => FollowAsync(active), CancellationToken.None);
    }

    private async Task FollowAsync(ActiveRun active)
    {
        RunResult? result = null;
        try
        {
            await foreach (var progress in active.Engine.WatchAsync(active.Handle, _stopping.Token))
            {
                result = progress.Result ?? result;
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

    private sealed class ActiveRun(Guid runId, Guid? backupJobId, RunHandle handle, IBackupEngine engine)
    {
        public Guid RunId { get; } = runId;
        public Guid? BackupJobId { get; } = backupJobId;
        public RunHandle Handle { get; } = handle;
        public IBackupEngine Engine { get; } = engine;
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
