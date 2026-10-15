using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.UI.Components.Tests.Infrastructure;

/// <summary>A bUnit context with the UI's services: a fake agent, a fixed clock (UTC), a fake picker and shell.</summary>
public abstract class UiTest : BunitContext
{
    private protected UiTest()
    {
        Agent = new FakeAgentClient();
        Monitor = new AgentMonitor(Agent);
        Services.AddSingleton<IAgentClient>(Agent);
        Services.AddSingleton(Monitor);
        Services.AddSingleton(new UiOptions { DashboardRefresh = Timeout.InfiniteTimeSpan });
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton<IFolderPicker>(Picker);
        Services.AddSingleton<IDesktopShell>(Shell);
    }

    private protected FakeAgentClient Agent { get; }
    private protected AgentMonitor Monitor { get; }
    private protected FixedTime Time { get; } = new(Sample.Now);
    private protected FakeFolderPicker Picker { get; } = new();
    private protected FakeShell Shell { get; } = new();

    private protected NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

    private protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>Lets the monitor ask the fake agent for its status, as the desktop shell does at start.</summary>
    private protected Task ConnectAsync() => Monitor.RefreshAsync(Ct);

    private protected static AgentCallException Error(string kind, string message, StatusCode code = StatusCode.FailedPrecondition) => new(code, kind, message);
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Answer { get; set; }

    public List<string> Titles { get; } = [];

    public Task<string?> PickFolderAsync(string title, string? initialFolder, CancellationToken ct)
    {
        Titles.Add(title);
        return Task.FromResult(Answer);
    }
}

internal sealed class FakeShell : IDesktopShell
{
    public string DefaultRestoreParent => @"C:\Users\pat\Restored";

    public List<string> Opened { get; } = [];

    public void OpenFolder(string path) => Opened.Add(path);
}

/// <summary>Test data. "Now" is Thursday 15 Oct 2026, 12:00 UTC, and the local time zone is UTC.</summary>
internal static class Sample
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static Timestamp At(DateTimeOffset when) => Timestamp.FromDateTimeOffset(when);

    public static Timestamp Today(int hour, int minute) => At(new DateTimeOffset(Now.Date.AddHours(hour).AddMinutes(minute), TimeSpan.Zero));

    public static Destination Destination(string id = "dest-1", string name = "Backup disk", string location = @"E:\Backups",
        DestinationState state = DestinationState.Connected) =>
        new() { Id = id, Name = name, Kind = DestinationKind.FileSystem, Location = location, State = state };

    public static Job Job(string id = "job-1", string name = "Documents", Run? lastRun = null, Schedule? schedule = null, Timestamp? nextRunAt = null,
        bool enabled = true, string destinationId = "dest-1", string source = @"C:\Users\pat\Documents") =>
        new()
        {
            Id = id,
            Spec = new JobSpec
            {
                Name = name, DestinationId = destinationId, SourcePath = source, Enabled = enabled, Compression = Compression.Balanced,
                Retention = new Retention { KeepLatest = 10, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 24, KeepAnnual = 3 },
                Schedule = schedule ?? new Schedule { Daily = new DailySchedule { MinuteOfDay = 12 * 60 } },
            },
            LastRun = lastRun,
            NextRunAt = nextRunAt,
            CreatedAt = At(Now.AddDays(-30)),
        };

    public static Run BackupRun(string id, RunState state, Timestamp started, string jobId = "job-1", long files = 120, long bytes = 5 * 1024 * 1024,
        params FailedItem[] failed)
    {
        var run = new Run
        {
            Id = id, JobId = jobId, Kind = RunKind.Backup, State = state, Trigger = RunTrigger.Scheduled, StartedAt = started,
            Files = files, Bytes = bytes, FailedItemCount = failed.Length,
        };
        if (state != RunState.Running)
        {
            run.FinishedAt = At(started.ToDateTimeOffset().AddMinutes(1));
            run.SnapshotId = state is RunState.Succeeded or RunState.CompletedWithWarnings ? "snap-" + id : "";
        }

        run.FailedItems.AddRange(failed);
        return run;
    }

    /// <summary>Puts a job and its runs (newest first) into the fake agent; the job's last run is the newest backup.</summary>
    public static Job AddJob(this FakeAgentClient agent, Job job, params Run[] runs)
    {
        job.LastRun ??= runs.FirstOrDefault(r => r.Kind == RunKind.Backup);
        agent.Jobs.Add(job);
        agent.Runs[job.Id] = [.. runs];
        return job;
    }
}
