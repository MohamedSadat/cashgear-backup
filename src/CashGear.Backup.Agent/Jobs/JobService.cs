using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Scheduling;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Jobs;

/// <summary>A job as the API creates or updates it.</summary>
internal sealed record JobInput(
    string Name,
    Guid DestinationId,
    string SourcePath,
    RetentionRule Retention,
    IReadOnlyList<string> Excludes,
    BackupCompression Compression,
    ScheduleSpec Schedule,
    bool Enabled);

/// <summary>A job with what the UI shows next to it.</summary>
internal sealed record JobView(JobRecord Job, RunRecord? LastRun, DateTimeOffset? NextRunAt);

internal static class JobMapping
{
    public static BackupJob ToEngineJob(JobRecord job) => new()
    {
        Id = job.Id,
        RepositoryId = job.DestinationId,
        Name = job.Name,
        SourcePath = job.SourcePath,
        Retention = new RetentionRule
        {
            KeepLatest = job.KeepLatest,
            KeepHourly = job.KeepHourly,
            KeepDaily = job.KeepDaily,
            KeepWeekly = job.KeepWeekly,
            KeepMonthly = job.KeepMonthly,
            KeepAnnual = job.KeepAnnual,
        },
        Excludes = job.Excludes,
        Compression = job.Compression,
    };

    public static void CopyTo(JobInput input, JobRecord job)
    {
        job.Name = input.Name.Trim();
        job.KeepLatest = input.Retention.KeepLatest;
        job.KeepHourly = input.Retention.KeepHourly;
        job.KeepDaily = input.Retention.KeepDaily;
        job.KeepWeekly = input.Retention.KeepWeekly;
        job.KeepMonthly = input.Retention.KeepMonthly;
        job.KeepAnnual = input.Retention.KeepAnnual;
        job.Excludes = [.. input.Excludes];
        job.Compression = input.Compression;
        input.Schedule.CopyTo(job);
        job.Enabled = input.Enabled;
    }
}

/// <summary>Creates, changes and removes jobs; keeps the engine policy and the schedule in step with state.db.</summary>
internal sealed partial class JobService(
    IDbContextFactory<StateDbContext> dbs, EngineHost engines, JobScheduler scheduler, RunCoordinator runs, TimeProvider time, ILogger<JobService> logger)
{
    public async Task<IReadOnlyList<JobView>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.Name).ToListAsync(ct);
        var views = new List<JobView>(jobs.Count);
        foreach (var job in jobs)
        {
            views.Add(await ViewAsync(db, job, ct));
        }

        return views;
    }

    public async Task<JobView> CreateAsync(JobInput input, CancellationToken ct)
    {
        var sourcePath = NormalizeSource(input.SourcePath);
        await using var db = await dbs.CreateDbContextAsync(ct);
        await ValidateAsync(db, input with { SourcePath = sourcePath }, ct);
        if (await db.Jobs.AnyAsync(j => j.DestinationId == input.DestinationId && j.SourcePath == sourcePath, ct))
        {
            // Two jobs on one folder would share one engine source and prune each other's snapshots.
            throw new AgentException(AgentError.AlreadyExists, $"A job already backs up {sourcePath} to this destination.");
        }

        var now = time.GetUtcNow();
        var job = new JobRecord { Id = Guid.NewGuid(), Name = input.Name, DestinationId = input.DestinationId, SourcePath = sourcePath, CreatedAt = now, UpdatedAt = now };
        JobMapping.CopyTo(input, job);
        db.Jobs.Add(job);
        await SaveAsync(db, ct);

        await ApplyPolicyAsync(job, ct);
        await scheduler.ScheduleAsync(job, now, ct);
        LogSaved(logger, job.Id, "created", job.SourcePath);
        return await ViewAsync(db, job, ct);
    }

    public async Task<JobView> UpdateAsync(Guid id, JobInput input, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var job = await db.Jobs.FindAsync([id], ct) ?? throw new AgentException(AgentError.NotFound, $"Job {id} does not exist.");
        if (input.DestinationId != job.DestinationId || !string.Equals(NormalizeSource(input.SourcePath), job.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            // Its snapshots are recorded under the old folder and destination; a new job keeps that history intact.
            throw new AgentException(AgentError.InvalidArgument, "A job's destination and source folder cannot change; create a new job instead.");
        }

        await ValidateAsync(db, input with { SourcePath = job.SourcePath }, ct);
        JobMapping.CopyTo(input, job);
        job.UpdatedAt = time.GetUtcNow();
        await SaveAsync(db, ct);

        await ApplyPolicyAsync(job, ct);
        await scheduler.ScheduleAsync(job, await SinceAsync(db, job, ct), ct);
        LogSaved(logger, job.Id, "updated", job.SourcePath);
        return await ViewAsync(db, job, ct);
    }

    /// <summary>Removes the job definition. Its snapshots stay in the repository and its runs in the history.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (runs.IsBackupRunning(id))
        {
            throw new AgentException(AgentError.FailedPrecondition, "The job is backing up now; cancel the run or wait for it to finish.");
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        var job = await db.Jobs.FindAsync([id], ct) ?? throw new AgentException(AgentError.NotFound, $"Job {id} does not exist.");
        db.Jobs.Remove(job);
        await db.SaveChangesAsync(ct);
        await scheduler.UnscheduleAsync(id, ct);
        LogSaved(logger, id, "deleted (backups kept)", job.SourcePath);
    }

    /// <summary>The last backup start, or the last change of the job if it never ran since: where catch-up counts from.</summary>
    public static async Task<DateTimeOffset> SinceAsync(StateDbContext db, JobRecord job, CancellationToken ct)
    {
        var lastStart = await db.Runs.Where(r => r.JobId == job.Id && r.Kind == RunKind.Backup)
            .OrderByDescending(r => r.StartedAt).Select(r => (DateTimeOffset?)r.StartedAt).FirstOrDefaultAsync(ct);
        return lastStart is { } last && last > job.UpdatedAt ? last : job.UpdatedAt;
    }

    private async Task<JobView> ViewAsync(StateDbContext db, JobRecord job, CancellationToken ct)
    {
        var lastRun = await db.Runs.AsNoTracking().Where(r => r.JobId == job.Id && r.Kind == RunKind.Backup)
            .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        return new JobView(job, lastRun, await scheduler.NextRunAsync(job.Id, ct));
    }

    private static async Task ValidateAsync(StateDbContext db, JobInput input, CancellationToken ct)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
        {
            errors.Add("A job needs a name of at most 200 characters.");
        }

        errors.AddRange(input.Schedule.Validate());
        errors.AddRange(new BackupJob
        {
            Id = Guid.NewGuid(),
            RepositoryId = input.DestinationId,
            Name = input.Name,
            SourcePath = input.SourcePath,
            Retention = input.Retention,
            Excludes = input.Excludes,
            Compression = input.Compression,
        }.Validate());
        if (errors.Count > 0)
        {
            throw new AgentException(AgentError.InvalidArgument, string.Join(" ", errors));
        }

        if (!await db.Destinations.AnyAsync(d => d.Id == input.DestinationId, ct))
        {
            throw new AgentException(AgentError.NotFound, $"Destination {input.DestinationId} does not exist.");
        }
    }

    /// <summary>Writes the job's policy now if its destination is connected; otherwise the next backup does.</summary>
    private async Task ApplyPolicyAsync(JobRecord job, CancellationToken ct)
    {
        if (!engines.IsConnected(job.DestinationId))
        {
            return;
        }

        try
        {
            await engines.Engine.ApplyJobPolicyAsync(JobMapping.ToEngineJob(job), ct);
        }
        catch (BackupEngineException ex) when (ex.Kind is BackupErrorKind.EngineUnavailable or BackupErrorKind.DestinationUnreachable)
        {
            LogPolicyDeferred(logger, job.Id, ex.Message);
        }
    }

    private static string NormalizeSource(string path) =>
        string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? path : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task SaveAsync(StateDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new AgentException(AgentError.AlreadyExists, "A job already backs up this folder to this destination. " + ex.InnerException?.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} {What}: {Source}")]
    private static partial void LogSaved(ILogger logger, Guid jobId, string what, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Policy of job {JobId} not written yet (destination unavailable); the next backup writes it: {Reason}")]
    private static partial void LogPolicyDeferred(ILogger logger, Guid jobId, string reason);
}
