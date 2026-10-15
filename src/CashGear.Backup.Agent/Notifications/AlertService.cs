using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Notifications;

/// <summary>Most serious first.</summary>
internal enum AlertKind
{
    VerifyFailed,
    BackupFailed,
    NoRecentBackup,
    BackupSkippedFiles,
}

/// <param name="Id">Stable while the condition lasts; a new failure gets a new id (emails go out once per id).</param>
internal sealed record AlertInfo(string Id, AlertKind Kind, Guid JobId, string JobName, string Message, DateTimeOffset Since);

/// <summary>
/// What needs the user's attention, worked out from state.db each time (nothing to keep in sync): per enabled job,
/// a failed or incomplete latest backup, a failed latest check, and no restore point for longer than the setting.
/// Paused jobs raise nothing.
/// </summary>
internal sealed class AlertService(IDbContextFactory<StateDbContext> dbs, SettingsStore settings, TimeProvider time)
{
    public async Task<IReadOnlyList<AlertInfo>> ListAsync(CancellationToken ct)
    {
        var alertAfter = TimeSpan.FromDays((await settings.LoadAsync(ct)).AlertAfterDays);
        var now = time.GetUtcNow();
        var alerts = new List<AlertInfo>();
        await using var db = await dbs.CreateDbContextAsync(ct);
        foreach (var job in await db.Jobs.AsNoTracking().Where(j => j.Enabled).ToListAsync(ct))
        {
            var runs = db.Runs.AsNoTracking().Where(r => r.JobId == job.Id && r.State != RunState.Running);
            var lastBackup = await runs.Where(r => r.Kind == RunKind.Backup).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            var lastPoint = await runs.Where(r => r.Kind == RunKind.Backup && (r.State == RunState.Succeeded || r.State == RunState.CompletedWithWarnings))
                .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            var lastCheck = await runs.Where(r => r.Kind == RunKind.Verify).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);

            if (lastCheck is { State: RunState.Failed })
            {
                alerts.Add(new AlertInfo($"verify-failed:{lastCheck.Id}", AlertKind.VerifyFailed, job.Id, job.Name,
                    lastCheck.Message ?? "The last check of this job's backups found a problem.", When(lastCheck)));
            }

            if (lastBackup is { State: RunState.Failed })
            {
                alerts.Add(new AlertInfo($"backup-failed:{lastBackup.Id}", AlertKind.BackupFailed, job.Id, job.Name,
                    lastBackup.Message ?? "The last backup failed.", When(lastBackup)));
            }
            else if (lastBackup is { State: RunState.CompletedWithWarnings })
            {
                alerts.Add(new AlertInfo($"backup-skipped:{lastBackup.Id}", AlertKind.BackupSkippedFiles, job.Id, job.Name,
                    $"The last backup could not read {lastBackup.FailedItemCount} file(s); the rest was backed up.", When(lastBackup)));
            }

            var since = lastPoint is null ? job.CreatedAt : When(lastPoint);
            if (now - since > alertAfter)
            {
                var days = (int)(now - since).TotalDays;
                alerts.Add(new AlertInfo($"no-recent-backup:{job.Id}:{lastPoint?.Id.ToString() ?? "none"}", AlertKind.NoRecentBackup, job.Id, job.Name,
                    lastPoint is null
                        ? $"No backup yet: the job was set up {days} days ago."
                        : $"The last backup was {days} days ago.",
                    since + alertAfter));
            }
        }

        return [.. alerts.OrderBy(a => a.Kind).ThenBy(a => a.JobName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static DateTimeOffset When(RunRecord run) => run.FinishedAt ?? run.StartedAt;
}
