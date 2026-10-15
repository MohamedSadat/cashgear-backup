using System.Globalization;
using System.Text;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Notifications;

/// <summary>
/// Every few minutes once the agent is ready: start checks of backups that are due, and email alerts that are new
/// since the last email (each alert once). The engine owns repository maintenance, so there is none here.
/// </summary>
internal sealed partial class Housekeeping(
    AgentOptions options,
    AgentState state,
    IDbContextFactory<StateDbContext> dbs,
    RunCoordinator runs,
    SettingsStore settings,
    AlertService alerts,
    IEmailSender email,
    Licensing.LicenceService licences,
    TimeProvider time,
    ILogger<Housekeeping> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>One round: due checks, then new alerts by email. Rounds never overlap.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await settings.LoadAsync(ct);
            await licences.RefreshIfDueAsync(ct);
            await StartDueChecksAsync(current, ct);
            await EmailNewAlertsAsync(current, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static (string Subject, string Body) TestEmail() => (
        "CashGear Backup: test email",
        $"This is a test from CashGear Backup on {Environment.MachineName}.{Environment.NewLine}Alerts about your backups will be sent to this address.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.HousekeepingInterval <= TimeSpan.Zero || options.HousekeepingInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(options.HousekeepingInterval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!state.Ready)
                {
                    continue;
                }

                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRoundFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>A job is due when it has a restore point and its last check (or its creation) is older than the setting.</summary>
    private async Task StartDueChecksAsync(AgentSettings current, CancellationToken ct)
    {
        if (current.VerifyEveryDays <= 0)
        {
            return;
        }

        var every = TimeSpan.FromDays(current.VerifyEveryDays);
        var now = time.GetUtcNow();
        var due = new List<Guid>();
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            foreach (var job in await db.Jobs.AsNoTracking().Where(j => j.Enabled).ToListAsync(ct))
            {
                var runsOfJob = db.Runs.AsNoTracking().Where(r => r.JobId == job.Id);
                if (!await runsOfJob.AnyAsync(r => r.Kind == RunKind.Backup && (r.State == RunState.Succeeded || r.State == RunState.CompletedWithWarnings), ct))
                {
                    continue;
                }

                var lastCheck = await runsOfJob.Where(r => r.Kind == RunKind.Verify).OrderByDescending(r => r.StartedAt)
                    .Select(r => (DateTimeOffset?)r.StartedAt).FirstOrDefaultAsync(ct);
                if (now - (lastCheck ?? job.CreatedAt) >= every && !runs.IsVerifying(job.Id))
                {
                    due.Add(job.Id);
                }
            }
        }

        foreach (var jobId in due)
        {
            try
            {
                await runs.StartVerifyAsync(jobId, Data.RunTrigger.Scheduled, current.VerifySamplePercent / 100, ct);
                LogCheckStarted(logger, jobId);
            }
            catch (AgentException ex)
            {
                LogCheckSkipped(logger, jobId, ex.Message);
            }
        }
    }

    private async Task EmailNewAlertsAsync(AgentSettings current, CancellationToken ct)
    {
        var active = await alerts.ListAsync(ct);
        var emailed = await settings.LoadEmailedAlertsAsync(ct);
        var stillActive = emailed.Where(id => active.Any(a => a.Id == id)).ToHashSet(StringComparer.Ordinal);
        var fresh = active.Where(a => !emailed.Contains(a.Id)).ToList();
        if (!current.Email.Enabled || fresh.Count == 0 || !(await licences.GetAsync(ct)).EmailAlerts)
        {
            if (stillActive.Count != emailed.Count)
            {
                await settings.SaveEmailedAlertsAsync(stillActive, ct);
            }

            return;
        }

        using var password = settings.EmailPassword(current);
        try
        {
            await email.SendAsync(current.Email, password, Subject(fresh.Count), Body(fresh), ct);
            stillActive.UnionWith(fresh.Select(a => a.Id));
            await settings.SaveEmailedAlertsAsync(stillActive, ct);
            LogEmailed(logger, fresh.Count);
        }
        catch (AgentException ex)
        {
            // Tried again next round.
            LogEmailFailed(logger, ex.Message);
        }
    }

    private static string Subject(int count) =>
        $"CashGear Backup: {count} {(count == 1 ? "problem needs" : "problems need")} your attention";

    private string Body(IReadOnlyList<AlertInfo> fresh)
    {
        var body = new StringBuilder();
        body.AppendLine(CultureInfo.InvariantCulture, $"CashGear Backup on {Environment.MachineName} found:");
        body.AppendLine();
        foreach (var alert in fresh)
        {
            var since = TimeZoneInfo.ConvertTime(alert.Since, time.LocalTimeZone).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
            body.AppendLine(CultureInfo.InvariantCulture, $"- {alert.JobName}: {alert.Message} (since {since})");
        }

        body.AppendLine();
        body.AppendLine("Open CashGear Backup on that PC for details.");
        return body.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled check of job {JobId} started")]
    private static partial void LogCheckStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled check of job {JobId} skipped: {Reason}")]
    private static partial void LogCheckSkipped(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emailed {Count} new alert(s)")]
    private static partial void LogEmailed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alert email not sent: {Reason}")]
    private static partial void LogEmailFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Housekeeping round failed")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}
