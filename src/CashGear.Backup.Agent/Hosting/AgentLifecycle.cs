using System.Security.Cryptography;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Scheduling;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Hosting;

/// <summary>What the agent reports about itself.</summary>
internal sealed class AgentState
{
    public DateTimeOffset StartedAt { get; set; }
    public string MachineIdentity { get; set; } = "";

    /// <summary>Destinations connected, jobs scheduled and missed runs started.</summary>
    public bool Ready { get; set; }
}

/// <summary>
/// Start: open state.db, record runs cut off by the last stop, start the engine and scheduler; then, in the
/// background, connect every destination, schedule every job and catch up runs missed while the agent was off.
/// Stop: scheduler, then runs (recorded as interrupted), then the engine.
/// </summary>
internal sealed partial class AgentLifecycle(
    AgentOptions options,
    IDbContextFactory<StateDbContext> dbs,
    AgentState state,
    EngineHost engines,
    JobScheduler scheduler,
    RunCoordinator runs,
    Licensing.LicenceService licences,
    TimeProvider time,
    ILogger<AgentLifecycle> logger) : IHostedService
{
    private const string MachineIdentityKey = "MachineIdentity";

    private readonly CancellationTokenSource _stopping = new();
    private Task _warmUp = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var dir in new[] { options.DataDirectory, options.EngineDirectory, options.LogDirectory })
        {
            Directory.CreateDirectory(dir);
        }

        await using (var db = await dbs.CreateDbContextAsync(cancellationToken))
        {
            await StateDb.MigrateAsync(db, cancellationToken);
            state.MachineIdentity = await MachineIdentityAsync(db, cancellationToken);
        }

        await runs.RecoverInterruptedAsync(cancellationToken);
        engines.Start(state.MachineIdentity);
        await scheduler.StartAsync(cancellationToken);
        state.StartedAt = time.GetUtcNow();
        LogStarted(logger, AgentInfo.Version, state.MachineIdentity, options.PipeName);
        _warmUp = Task.Run(() => WarmUpAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _warmUp;
        await scheduler.StopAsync(cancellationToken);
        await runs.StopAsync();
        await engines.DisposeAsync();
        LogStopped(logger);
    }

    private async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            await licences.ActivatePreseededKeyAsync(ct);
            await using var db = await dbs.CreateDbContextAsync(ct);
            foreach (var destination in await db.Destinations.AsNoTracking().ToListAsync(ct))
            {
                try
                {
                    await engines.ConnectAsync(destination, ct);
                }
                catch (BackupEngineException)
                {
                    // Logged by EngineHost; a backup retries the connection.
                }
            }

            var due = new List<JobRecord>();
            var now = time.GetUtcNow();
            foreach (var job in await db.Jobs.AsNoTracking().ToListAsync(ct))
            {
                var since = await JobService.SinceAsync(db, job, ct);
                await scheduler.ScheduleAsync(job, since, ct);
                if (job.Enabled && ScheduleMath.IsDue(ScheduleSpec.From(job), since, now, JobScheduler.Zone))
                {
                    due.Add(job);
                }
            }

            foreach (var job in due)
            {
                LogCatchUp(logger, job.Id);
                try
                {
                    await runs.StartBackupAsync(job.Id, RunTrigger.CatchUp, ct);
                }
                catch (AgentException ex)
                {
                    LogCatchUpSkipped(logger, job.Id, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogWarmUpFailed(logger, ex);
        }

        state.Ready = true;
    }

    /// <summary>Generated once and kept: snapshots are recorded under it, so renaming the PC must not change it.</summary>
    private static async Task<string> MachineIdentityAsync(StateDbContext db, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync([MachineIdentityKey], ct);
        if (setting is null)
        {
            setting = new SettingRecord { Key = MachineIdentityKey, Value = "cg-" + RandomNumberGenerator.GetHexString(16, lowercase: true) };
            db.Settings.Add(setting);
            await db.SaveChangesAsync(ct);
        }

        return setting.Value;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CashGear Backup agent {Version} started as {MachineIdentity} on pipe {PipeName}")]
    private static partial void LogStarted(ILogger logger, string version, string machineIdentity, string pipeName);

    [LoggerMessage(Level = LogLevel.Information, Message = "CashGear Backup agent stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} missed a scheduled run while the agent was off; running it now")]
    private static partial void LogCatchUp(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catch-up run of job {JobId} skipped: {Reason}")]
    private static partial void LogCatchUpSkipped(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent warm-up failed; destinations or schedules may be missing until the next start")]
    private static partial void LogWarmUpFailed(ILogger logger, Exception exception);
}
