using System.Security.Principal;
using System.Text;
using CashGear.Backup.Agent.Scheduling;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Data;
using CashGear.Backup.Testing;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DestinationKind = CashGear.Backup.Contracts.V1.DestinationKind;
using RunTrigger = CashGear.Backup.Contracts.V1.RunTrigger;

namespace CashGear.Backup.Agent.Tests;

/// <summary>The agent as the desktop app sees it: gRPC over the named pipe, real kopia.exe underneath.</summary>
[Trait("Category", "Integration")]
public class AgentEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_backup_browse_restore_and_history_through_the_pipe()
    {
        await using var h = await AgentHarness.StartAsync(nameof(First_backup_browse_restore_and_history_through_the_pipe));
        TreeFixture.Create(h.Source);
        var manifest = TreeManifest.Capture(h.Source);

        var status = await h.Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct);
        Assert.True(status.Ready);
        Assert.StartsWith("cg-", status.MachineIdentity, StringComparison.Ordinal);
        Assert.True(status.CallerCanManage);

        var destination = await h.CreateDestinationAsync();
        Assert.Equal(DestinationState.Connected, destination.State);
        Assert.Equal(h.Destination, destination.Location);

        var job = await h.CreateJobAsync(destination.Id, new Schedule { Daily = new DailySchedule { MinuteOfDay = 2 * 60 } });
        Assert.NotNull(job.NextRunAt);
        Assert.True(job.NextRunAt.ToDateTimeOffset() > DateTimeOffset.UtcNow);

        var started = await h.Client.RunJobNowAsync(new RunJobNowRequest { JobId = job.Id }, cancellationToken: Ct);
        Assert.Equal(RunState.Running, started.State);
        Assert.Equal(RunTrigger.Manual, started.Trigger);
        var (updates, backup) = await h.WatchAsync(started.Id);
        Assert.Equal(RunState.Succeeded, backup.State);
        Assert.False(string.IsNullOrEmpty(backup.SnapshotId));
        Assert.Equal(manifest.FileCount, backup.Files);
        Assert.NotNull(backup.FinishedAt);
        Assert.Equal(manifest.FileCount, updates[^1].FilesProcessed);

        // A finished run can still be watched: one message with the result.
        var (again, sameRun) = await h.WatchAsync(started.Id);
        Assert.Single(again);
        Assert.Equal(backup, sameRun);

        var history = (await h.Client.ListRunsAsync(new ListRunsRequest { JobId = job.Id }, cancellationToken: Ct)).Runs;
        Assert.Equal(backup, Assert.Single(history));
        Assert.Equal(backup, (await h.Client.ListJobsAsync(new ListJobsRequest(), cancellationToken: Ct)).Jobs.Single().LastRun);

        var snapshot = Assert.Single((await h.Client.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = job.Id }, cancellationToken: Ct)).Snapshots);
        Assert.Equal(backup.SnapshotId, snapshot.Id);
        Assert.True(snapshot.Complete);

        var root = (await h.Client.BrowseSnapshotAsync(new BrowseSnapshotRequest { JobId = job.Id, SnapshotId = snapshot.Id }, cancellationToken: Ct)).Entries;
        Assert.Equal(EntryKind.Directory, root.Single(e => e.Name == "many").Kind);
        var many = (await h.Client.BrowseSnapshotAsync(new BrowseSnapshotRequest { JobId = job.Id, SnapshotId = snapshot.Id, Path = "many" }, cancellationToken: Ct)).Entries;
        Assert.Equal(200, many.Count);

        var target = Path.Combine(h.Restores, "full");
        var restoreRun = await h.Client.StartRestoreAsync(new StartRestoreRequest { JobId = job.Id, SnapshotId = snapshot.Id, TargetFolder = target }, cancellationToken: Ct);
        Assert.Equal(RunKind.Restore, restoreRun.Kind);
        var restored = (await h.WatchAsync(restoreRun.Id)).Finished;
        Assert.Equal(RunState.Succeeded, restored.State);
        Assert.Equal(target, restored.RestoreTarget);
        Assert.Equal(snapshot.Id, restored.SnapshotId);
        var differences = manifest.ContentDifferences(TreeManifest.Capture(target));
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences.Take(10)));

        var ex = await Assert.ThrowsAsync<RpcException>(() => h.Client.StartRestoreAsync(
            new StartRestoreRequest { JobId = job.Id, SnapshotId = snapshot.Id, TargetFolder = target }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal("RestoreTargetNotEmpty", AgentHarness.ErrorKind(ex));
    }

    [Fact]
    public async Task A_restart_reconnects_destinations_from_protected_passwords_and_keeps_history()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_restart_reconnects_destinations_from_protected_passwords_and_keeps_history));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "first");
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);
        var first = await h.RunNowAsync(job.Id);
        Assert.Equal(RunState.Succeeded, first.State);

        await h.RestartAsync();

        var reconnected = Assert.Single((await h.Client.ListDestinationsAsync(new ListDestinationsRequest(), cancellationToken: Ct)).Destinations);
        Assert.Equal(DestinationState.Connected, reconnected.State);
        Assert.Equal(first, Assert.Single((await h.Client.ListRunsAsync(new ListRunsRequest(), cancellationToken: Ct)).Runs));

        File.WriteAllText(Path.Combine(h.Source, "b.txt"), "second");
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);
        Assert.Equal(2, (await h.Client.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = job.Id }, cancellationToken: Ct)).Snapshots.Count);
    }

    [Fact]
    public async Task A_run_cut_off_by_an_agent_crash_is_recorded_as_failed_at_the_next_start()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_run_cut_off_by_an_agent_crash_is_recorded_as_failed_at_the_next_start));
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);
        await h.StopAgentAsync();

        var orphan = Guid.NewGuid();
        await h.WithStateDbAsync(async db =>
        {
            db.Runs.Add(new RunRecord { Id = orphan, JobId = Guid.Parse(job.Id), Kind = Core.RunKind.Backup, State = Core.RunState.Running, Trigger = Data.RunTrigger.Scheduled, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync(Ct);
        });
        await h.StartAgentAsync();

        var run = Assert.Single((await h.Client.ListRunsAsync(new ListRunsRequest(), cancellationToken: Ct)).Runs);
        Assert.Equal(orphan.ToString(), run.Id);
        Assert.Equal(RunState.Failed, run.State);
        Assert.Contains("agent stopped", run.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task A_schedule_missed_while_the_agent_was_off_runs_once_at_start()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_schedule_missed_while_the_agent_was_off_runs_once_at_start));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "a");
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id, new Schedule { Interval = new IntervalSchedule { Hours = 1 } });
        await h.StopAgentAsync();

        // The PC was off for three hours.
        await h.WithStateDbAsync(async db =>
        {
            var record = await db.Jobs.SingleAsync(Ct);
            record.CreatedAt = record.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3);
            await db.SaveChangesAsync(Ct);
        });
        await h.StartAgentAsync();

        var run = await WaitForSingleRunAsync(h);
        Assert.Equal(RunTrigger.CatchUp, run.Trigger);
        Assert.Equal(RunState.Succeeded, (await h.WatchAsync(run.Id)).Finished.State);

        // Caught up: a second start does not run it again.
        await h.RestartAsync();
        await Task.Delay(1000, Ct);
        Assert.Single((await h.Client.ListRunsAsync(new ListRunsRequest(), cancellationToken: Ct)).Runs);
    }

    [Fact]
    public async Task The_scheduler_firing_a_job_runs_its_backup()
    {
        await using var h = await AgentHarness.StartAsync(nameof(The_scheduler_firing_a_job_runs_its_backup));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "a");
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id, new Schedule { Daily = new DailySchedule { MinuteOfDay = 0, DaysOfWeek = { 0, 3 } } });

        await h.App.Services.GetRequiredService<JobScheduler>().TriggerNowAsync(Guid.Parse(job.Id), Ct);

        var run = await WaitForSingleRunAsync(h);
        Assert.Equal(RunTrigger.Scheduled, run.Trigger);
        Assert.Equal(RunState.Succeeded, (await h.WatchAsync(run.Id)).Finished.State);
    }

    [Fact]
    public async Task Deleting_a_job_keeps_its_backups_and_history()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Deleting_a_job_keeps_its_backups_and_history));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "a");
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);
        var run = await h.RunNowAsync(job.Id);

        await h.Client.DeleteJobAsync(new DeleteJobRequest { Id = job.Id }, cancellationToken: Ct);
        Assert.Empty((await h.Client.ListJobsAsync(new ListJobsRequest(), cancellationToken: Ct)).Jobs);
        Assert.Equal(run.Id, Assert.Single((await h.Client.ListRunsAsync(new ListRunsRequest(), cancellationToken: Ct)).Runs).Id);

        // The same folder in a new job finds the earlier restore point.
        var again = await h.CreateJobAsync(destination.Id);
        Assert.Equal(run.SnapshotId, Assert.Single((await h.Client.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = again.Id }, cancellationToken: Ct)).Snapshots).Id);
    }

    [Fact]
    public async Task Jobs_are_validated_and_cannot_share_or_change_their_source()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Jobs_are_validated_and_cannot_share_or_change_their_source));
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);

        var duplicate = await Assert.ThrowsAsync<RpcException>(() => h.CreateJobAsync(destination.Id));
        Assert.Equal(StatusCode.AlreadyExists, duplicate.StatusCode);

        var moved = AgentHarness.Spec(destination.Id, Path.Combine(h.Root, "elsewhere"));
        var change = await Assert.ThrowsAsync<RpcException>(() => h.Client.UpdateJobAsync(new UpdateJobRequest { Id = job.Id, Spec = moved }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, change.StatusCode);

        var bad = AgentHarness.Spec(destination.Id, Path.Combine(h.Root, "other"));
        bad.Retention.KeepLatest = 0;
        var retention = await Assert.ThrowsAsync<RpcException>(() => h.Client.CreateJobAsync(new CreateJobRequest { Spec = bad }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, retention.StatusCode);
        Assert.Contains("KeepLatest", retention.Status.Detail, StringComparison.Ordinal);

        var relative = AgentHarness.Spec(destination.Id, "Documents");
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(() => h.Client.CreateJobAsync(new CreateJobRequest { Spec = relative }, cancellationToken: Ct).ResponseAsync)).StatusCode);

        var updated = AgentHarness.Spec(destination.Id, h.Source, new Schedule { Interval = new IntervalSchedule { Hours = 6 } });
        updated.Retention.KeepLatest = 3;
        var saved = await h.Client.UpdateJobAsync(new UpdateJobRequest { Id = job.Id, Spec = updated }, cancellationToken: Ct);
        Assert.Equal(3, saved.Spec.Retention.KeepLatest);
        Assert.Equal(6, saved.Spec.Schedule.Interval.Hours);
        Assert.NotNull(saved.NextRunAt);
    }

    [Fact]
    public async Task An_unreachable_destination_records_a_failed_run_and_recovers()
    {
        await using var h = await AgentHarness.StartAsync(nameof(An_unreachable_destination_records_a_failed_run_and_recovers));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "a");
        var location = Path.Combine(h.Root, "usb-drive");
        var destination = await h.CreateDestinationAsync(location);
        var job = await h.CreateJobAsync(destination.Id);

        Directory.Move(location, location + "-unplugged");
        var failed = await h.RunNowAsync(job.Id);
        Assert.Equal(RunState.Failed, failed.State);
        Assert.Contains("not reachable", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(failed.SnapshotId));

        Directory.Move(location + "-unplugged", location);
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);
    }

    [Fact]
    public async Task Connecting_with_a_wrong_password_is_refused_and_stores_nothing()
    {
        await using var first = await AgentHarness.StartAsync(nameof(Connecting_with_a_wrong_password_is_refused_and_stores_nothing));
        await first.CreateDestinationAsync();

        await using var second = await AgentHarness.StartAsync("second-pc");
        var wrong = new ConnectDestinationRequest { Name = "NAS", Kind = DestinationKind.FileSystem, Location = first.Destination, Password = "not-the-password" };
        var ex = await Assert.ThrowsAsync<RpcException>(() => second.Client.ConnectDestinationAsync(wrong, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("InvalidPassword", AgentHarness.ErrorKind(ex));
        Assert.Empty((await second.Client.ListDestinationsAsync(new ListDestinationsRequest(), cancellationToken: Ct)).Destinations);

        var right = wrong.Clone();
        right.Password = first.Password;
        Assert.Equal(DestinationState.Connected, (await second.Client.ConnectDestinationAsync(right, cancellationToken: Ct)).State);

        var duplicate = await Assert.ThrowsAsync<RpcException>(() => second.Client.ConnectDestinationAsync(right, cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.AlreadyExists, duplicate.StatusCode);
    }

    [Fact]
    public async Task Passwords_never_reach_state_db_logs_or_engine_files()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Passwords_never_reach_state_db_logs_or_engine_files));
        File.WriteAllText(Path.Combine(h.Source, "a.txt"), "a");
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);
        await h.RunNowAsync(job.Id);
        await h.StopAgentAsync();

        var needles = new[] { Encoding.UTF8.GetBytes(h.Password), Encoding.Unicode.GetBytes(h.Password), Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(h.Password))) };
        var files = Directory.EnumerateFiles(h.Data, "*", SearchOption.AllDirectories).ToList();
        Assert.Contains(files, f => f.EndsWith(StateDb.FileName, StringComparison.Ordinal));
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("agent-", StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal));
        var leaks = files.Where(f => needles.Any(n => File.ReadAllBytes(f).AsSpan().IndexOf(n) >= 0)).ToList();
        Assert.True(leaks.Count == 0, "password found in: " + string.Join(", ", leaks));
    }

    [Fact]
    public async Task Manage_calls_need_an_elevated_administrator_by_default()
    {
        Assert.SkipWhen(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "the test process is elevated, so the default policy would allow it");
        await using var h = await AgentHarness.StartAsync(nameof(Manage_calls_need_an_elevated_administrator_by_default), o => o.AllowNonElevatedManage = false);

        Assert.False((await h.Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct)).CallerCanManage);
        Assert.Empty((await h.Client.ListJobsAsync(new ListJobsRequest(), cancellationToken: Ct)).Jobs);
        var ex = await Assert.ThrowsAsync<RpcException>(() => h.CreateDestinationAsync());
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(h.Destination));
    }

    [Fact]
    public async Task The_agent_listens_on_its_named_pipe_only()
    {
        await using var h = await AgentHarness.StartAsync(nameof(The_agent_listens_on_its_named_pipe_only));
        var addresses = h.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.NotEmpty(addresses);
        Assert.All(addresses, a => Assert.StartsWith("http://pipe:/", a, StringComparison.Ordinal));
    }

    private static async Task<Contracts.V1.Run> WaitForSingleRunAsync(AgentHarness h)
    {
        for (var i = 0; i < 200; i++)
        {
            var runs = (await h.Client.ListRunsAsync(new ListRunsRequest(), cancellationToken: Ct)).Runs;
            if (runs.Count > 0)
            {
                return Assert.Single(runs);
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("no run started");
    }
}
