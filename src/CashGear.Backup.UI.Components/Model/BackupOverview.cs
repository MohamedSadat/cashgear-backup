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

/// <summary>How serious each kind of alert is.</summary>
public static class Alerts
{
    public static bool IsProblem(AlertKind kind) => kind is AlertKind.VerifyFailed or AlertKind.BackupFailed;

    public static int Rank(AlertKind kind) => kind switch
    {
        AlertKind.VerifyFailed => 0,
        AlertKind.BackupFailed => 1,
        AlertKind.NoRecentBackup => 2,
        _ => 3,
    };

    /// <summary>The badge or panel style: error or warn.</summary>
    public static string Tone(AlertKind kind) => IsProblem(kind) ? "error" : "warn";
}

/// <summary>One line about all backups, for the tray icon.</summary>
public sealed record BackupOverview(OverviewLevel Level, string Text)
{
    /// <summary>Once the agent is ready, its alerts decide: the most serious one is shown.</summary>
    public static BackupOverview Summarize(AgentConnection connection, IReadOnlyList<Job>? jobs, IReadOnlyList<Alert>? alerts)
    {
        if (connection != AgentConnection.Ready || alerts is not { Count: > 0 })
        {
            return Summarize(connection, jobs);
        }

        var top = alerts.OrderBy(a => Alerts.Rank(a.Kind)).First();
        return new(Alerts.IsProblem(top.Kind) ? OverviewLevel.Problem : OverviewLevel.Warning, $"{top.JobName}: {top.Message}");
    }

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
