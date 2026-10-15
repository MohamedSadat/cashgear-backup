using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Services;

namespace CashGear.Backup.UI.Components.Model;

public enum OverviewLevel
{
    Ok,
    Info,
    Warning,
    Problem,
}

/// <summary>One line about all backups, for the tray icon.</summary>
public sealed record BackupOverview(OverviewLevel Level, string Text)
{
    public static BackupOverview Summarize(AgentConnection connection, IReadOnlyList<Job>? jobs, IReadOnlyList<Alert> alerts) => Summarize(connection, jobs);

    public static BackupOverview Summarize(AgentConnection connection, IReadOnlyList<Job>? jobs)
    {
        switch (connection)
        {
            case AgentConnection.NotRunning:
                return new(OverviewLevel.Problem, "The backup service is not running");
            case AgentConnection.NotTrusted:
                return new(OverviewLevel.Problem, "The backup service could not be verified");
            case AgentConnection.Unknown:
            case AgentConnection.Starting:
                return new(OverviewLevel.Info, "The backup service is starting");
        }

        if (jobs is null)
        {
            return new(OverviewLevel.Info, "Checking your backups");
        }

        if (jobs.Count == 0)
        {
            return new(OverviewLevel.Warning, "No backups set up yet");
        }

        if (jobs.FirstOrDefault(j => j.LastRun?.State == RunState.Running) is { } running)
        {
            return new(OverviewLevel.Info, $"Backing up {running.Spec.Name}");
        }

        if (jobs.FirstOrDefault(j => j.LastRun?.State == RunState.Failed) is { } failed)
        {
            return new(OverviewLevel.Problem, $"{failed.Spec.Name}: the last backup failed");
        }

        if (jobs.FirstOrDefault(j => j.LastRun?.State == RunState.CompletedWithWarnings) is { } warned)
        {
            return new(OverviewLevel.Warning, $"{warned.Spec.Name}: the last backup skipped some files");
        }

        if (jobs.FirstOrDefault(j => j.LastRun?.State == RunState.Canceled) is { } canceled)
        {
            return new(OverviewLevel.Warning, $"{canceled.Spec.Name}: the last backup was canceled");
        }

        if (jobs.FirstOrDefault(j => j.LastRun is null && j.Spec.Enabled) is { } never)
        {
            return new(OverviewLevel.Warning, $"{never.Spec.Name} has not been backed up yet");
        }

        return new(OverviewLevel.Ok, "All backups are up to date");
    }
}
