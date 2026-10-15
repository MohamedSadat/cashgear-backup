using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Data;
using Quartz;

namespace CashGear.Backup.Agent.Scheduling;

/// <summary>
/// Quartz triggers for the jobs in state.db. state.db is the only source of truth: the scheduler uses
/// Quartz's in-memory store, is rebuilt at every start, and runs missed while the agent was off are
/// caught up from the run history (<see cref="ScheduleMath.IsDue"/>), not from a Quartz job store.
/// </summary>
internal sealed partial class JobScheduler(ISchedulerFactory factory, TimeProvider time, ILogger<JobScheduler> logger)
{
    private const string Group = "backup";
    private IScheduler? _scheduler;

    public static TimeZoneInfo Zone => TimeZoneInfo.Local;

    public async Task StartAsync(CancellationToken ct)
    {
        _scheduler = await factory.GetScheduler(ct);
        await _scheduler.Start(ct);
    }

    /// <summary>(Re)creates the job's trigger. <paramref name="since"/> is its last run or last change.</summary>
    public async Task ScheduleAsync(JobRecord job, DateTimeOffset since, CancellationToken ct)
    {
        var scheduler = Scheduler;
        var key = Key(job.Id);
        await scheduler.DeleteJob(key, ct);
        var schedule = ScheduleSpec.From(job);
        if (!job.Enabled || schedule.Kind == ScheduleKind.Manual)
        {
            return;
        }

        var detail = JobBuilder.Create<BackupQuartzJob>()
            .WithIdentity(key)
            .UsingJobData(BackupQuartzJob.JobIdKey, job.Id.ToString())
            .Build();
        var trigger = TriggerBuilder.Create().WithIdentity(job.Id.ToString("N"), Group).ForJob(key);
        if (schedule.Kind == ScheduleKind.Daily)
        {
            // After sleep or a busy period: fire once now, then continue on the normal times.
            trigger = trigger.WithCronSchedule(ScheduleMath.Cron(schedule)!, c => c.InTimeZone(Zone).WithMisfireHandlingInstructionFireAndProceed());
        }
        else
        {
            // Missed (sleep): fire once now, then every N hours from then.
            var now = time.GetUtcNow();
            var start = ScheduleMath.NextAfter(schedule, since, Zone)!.Value;
            trigger = trigger
                .StartAt(start > now ? start : now + TimeSpan.FromHours(schedule.IntervalHours))
                .WithSimpleSchedule(s => s.WithIntervalInHours(schedule.IntervalHours).RepeatForever().WithMisfireHandlingInstructionNowWithExistingCount());
        }

        await scheduler.ScheduleJob(detail, trigger.Build(), ct);
        LogScheduled(logger, job.Id, schedule.Kind, await NextRunAsync(job.Id, ct));
    }

    public async Task UnscheduleAsync(Guid jobId, CancellationToken ct) => await Scheduler.DeleteJob(Key(jobId), ct);

    public async Task<DateTimeOffset?> NextRunAsync(Guid jobId, CancellationToken ct)
    {
        var triggers = await Scheduler.GetTriggersOfJob(Key(jobId), ct);
        return triggers.Select(t => t.GetNextFireTimeUtc()).Where(t => t is not null).Min();
    }

    /// <summary>Fires the job's trigger now, as the schedule would (tests and diagnostics).</summary>
    public async Task TriggerNowAsync(Guid jobId, CancellationToken ct)
    {
        if (!await Scheduler.CheckExists(Key(jobId), ct))
        {
            throw new AgentException(AgentError.FailedPrecondition, $"Job {jobId} has no schedule.");
        }

        await Scheduler.TriggerJob(Key(jobId), ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_scheduler is { IsShutdown: false })
        {
            await _scheduler.Shutdown(waitForJobsToComplete: true, ct);
        }
    }

    private IScheduler Scheduler => _scheduler ?? throw new InvalidOperationException("The scheduler has not started.");

    private static JobKey Key(Guid jobId) => new(jobId.ToString("N"), Group);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} scheduled ({Kind}); next run {NextRun}")]
    private static partial void LogScheduled(ILogger logger, Guid jobId, ScheduleKind kind, DateTimeOffset? nextRun);
}

/// <summary>What Quartz runs when a job's time comes: start its backup (the run continues in the background).</summary>
[DisallowConcurrentExecution]
internal sealed partial class BackupQuartzJob(RunCoordinator runs, ILogger<BackupQuartzJob> logger) : IJob
{
    public const string JobIdKey = "jobId";

    public async Task Execute(IJobExecutionContext context)
    {
        var jobId = Guid.Parse(context.MergedJobDataMap.GetString(JobIdKey)!);
        try
        {
            await runs.StartBackupAsync(jobId, RunTrigger.Scheduled, context.CancellationToken);
        }
        catch (AgentException ex)
        {
            // Already running, or deleted a moment ago: nothing to do until the next time.
            LogSkipped(logger, jobId, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled backup of job {JobId} skipped: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid jobId, string reason);
}
