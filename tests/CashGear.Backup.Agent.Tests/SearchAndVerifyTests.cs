using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using AlertKind = CashGear.Backup.Contracts.V1.AlertKind;
using CashGear.Backup.Testing;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.Agent.Tests;

/// <summary>Search inside a snapshot, and checks of a job's backups (on demand and on schedule).</summary>
[Trait("Category", "Integration")]
public class SearchAndVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Finds_names_anywhere_in_a_snapshot_in_any_case_and_stops_at_the_limit()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Finds_names_anywhere_in_a_snapshot_in_any_case_and_stops_at_the_limit));
        TreeFixture.Create(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await RestoreToOriginalTests.BackUpAsync(h, job);

        var deep = await SearchAsync(h, job, snapshot, "DEEP");
        Assert.Contains(deep.Matches, m => m.Path == TreeFixture.DeepFile && m.Kind == EntryKind.File);
        Assert.False(deep.Truncated);
        Assert.True(deep.FoldersSearched > 3);

        var limited = await SearchAsync(h, job, snapshot, "file-", maxResults: 3);
        Assert.Equal(3, limited.Matches.Count);
        Assert.True(limited.Truncated);

        var ex = await Assert.ThrowsAsync<RpcException>(() => SearchAsync(h, job, snapshot, "x"));
        Assert.Equal("InvalidArgument", AgentHarness.ErrorKind(ex));
    }

    [Fact]
    public async Task A_check_of_a_healthy_backup_passes_and_damage_is_reported_and_raises_an_alert()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_check_of_a_healthy_backup_passes_and_damage_is_reported_and_raises_an_alert));
        RestoreToOriginalTests.WriteSource(h.Source);
        // Big enough random files that damage to the packs hits file contents, not padding.
        var random = new Random(20261015);
        for (var i = 0; i < 6; i++)
        {
            var bytes = new byte[64 * 1024];
            random.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(h.Source, $"data-{i}.bin"), bytes, Ct);
        }

        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        await RestoreToOriginalTests.BackUpAsync(h, job);
        await SetSamplePercentAsync(h, 100);

        var started = await h.Client.VerifyJobNowAsync(new VerifyJobNowRequest { JobId = job.Id }, cancellationToken: Ct);
        Assert.Equal(RunKind.Verify, started.Kind);
        var passed = (await h.WatchAsync(started.Id)).Finished;
        Assert.Equal(RunState.Succeeded, passed.State);
        Assert.Equal(9, passed.Files);
        Assert.Contains(passed, (await h.Client.ListRunsAsync(new ListRunsRequest { JobId = job.Id }, cancellationToken: Ct)).Runs);
        // A check is not a backup: the job's last backup is unchanged.
        Assert.Equal(RunKind.Backup, (await h.Client.ListJobsAsync(new ListJobsRequest(), cancellationToken: Ct)).Jobs[0].LastRun.Kind);

        CorruptDataPacks(h.Destination);
        var failed = (await h.WatchAsync((await h.Client.VerifyJobNowAsync(new VerifyJobNowRequest { JobId = job.Id }, cancellationToken: Ct)).Id)).Finished;
        Assert.Equal(RunState.Failed, failed.State);
        Assert.True(failed.FailedItemCount > 0);
        Assert.NotEmpty(failed.FailedItems);

        var alert = Assert.Single((await h.Client.ListAlertsAsync(new ListAlertsRequest(), cancellationToken: Ct)).Alerts, a => a.Kind == AlertKind.VerifyFailed);
        Assert.Equal(job.Id, alert.JobId);
        Assert.Equal("Documents", alert.JobName);
    }

    [Fact]
    public async Task Checks_run_on_schedule_once_due()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Checks_run_on_schedule_once_due), o => o.HousekeepingInterval = Timeout.InfiniteTimeSpan);
        RestoreToOriginalTests.WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        await RestoreToOriginalTests.BackUpAsync(h, job);
        var housekeeping = h.App.Services.GetRequiredService<Housekeeping>();

        // A new job is not due yet (the first check comes verify_every_days after it was set up).
        await housekeeping.RunOnceAsync(Ct);
        Assert.DoesNotContain((await h.Client.ListRunsAsync(new ListRunsRequest { JobId = job.Id }, cancellationToken: Ct)).Runs, r => r.Kind == RunKind.Verify);

        await h.StopAgentAsync();
        await h.WithStateDbAsync(async db =>
        {
            var stored = await db.Jobs.FindAsync([Guid.Parse(job.Id)], Ct);
            stored!.CreatedAt = stored.CreatedAt.AddDays(-8);
            stored.UpdatedAt = stored.CreatedAt;
            await db.SaveChangesAsync(Ct);
        });
        await h.StartAgentAsync();
        await h.App.Services.GetRequiredService<Housekeeping>().RunOnceAsync(Ct);

        var verify = Assert.Single((await h.Client.ListRunsAsync(new ListRunsRequest { JobId = job.Id }, cancellationToken: Ct)).Runs, r => r.Kind == RunKind.Verify);
        Assert.Equal(RunTrigger.Scheduled, verify.Trigger);
        Assert.Equal(RunState.Succeeded, (await h.WatchAsync(verify.Id)).Finished.State);
    }

    internal static async Task SetSamplePercentAsync(AgentHarness h, double percent)
    {
        var settings = await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        settings.VerifySamplePercent = percent;
        await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = settings }, cancellationToken: Ct);
    }

    private static async Task<(List<SnapshotEntry> Matches, bool Truncated, long FoldersSearched)> SearchAsync(
        AgentHarness h, Job job, Snapshot snapshot, string query, int maxResults = 0)
    {
        using var call = h.Client.SearchSnapshot(new SearchSnapshotRequest { JobId = job.Id, SnapshotId = snapshot.Id, Query = query, MaxResults = maxResults }, cancellationToken: Ct);
        var matches = new List<SnapshotEntry>();
        SearchSnapshotResponse? last = null;
        await foreach (var response in call.ResponseStream.ReadAllAsync(Ct))
        {
            matches.AddRange(response.Matches);
            last = response;
        }

        Assert.NotNull(last);
        Assert.True(last.Done);
        return (matches, last.Truncated, last.FoldersSearched);
    }

    private static void CorruptDataPacks(string repository)
    {
        foreach (var pack in Directory.GetFiles(Path.Combine(repository, "p"), "*.f", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(pack);
            for (var i = bytes.Length / 3; i < bytes.Length / 3 + 256 && i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;
            }

            File.WriteAllBytes(pack, bytes);
        }
    }
}
