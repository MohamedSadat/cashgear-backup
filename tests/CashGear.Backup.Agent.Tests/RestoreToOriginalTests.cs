using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Agent.Tests;

/// <summary>
/// Restoring back into the job's folder: nothing there is replaced. Files that differ are kept and the restored
/// copy goes next to them; identical files are left alone; missing files come back.
/// </summary>
[Trait("Category", "Integration")]
public class RestoreToOriginalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keeps_both_versions_brings_back_missing_files_and_leaves_identical_ones()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Keeps_both_versions_brings_back_missing_files_and_leaves_identical_ones));
        WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await BackUpAsync(h, job);

        await File.WriteAllTextAsync(Path.Combine(h.Source, "docs", "a.txt"), "alpha, edited after the backup", Ct);
        File.Delete(Path.Combine(h.Source, "docs", "b.txt"));
        await File.WriteAllTextAsync(Path.Combine(h.Source, "docs", "new.txt"), "made after the backup", Ct);

        var run = await RestoreToOriginalAsync(h, job, snapshot, "docs");

        Assert.Equal(RunState.Succeeded, run.State);
        Assert.Equal(Path.Combine(h.Source, "docs"), run.RestoreTarget);
        var docs = Path.Combine(h.Source, "docs");
        Assert.Equal("alpha, edited after the backup", await File.ReadAllTextAsync(Path.Combine(docs, "a.txt"), Ct));
        var kept = Assert.Single(Directory.GetFiles(docs, "a (restored *).txt"));
        Assert.Equal("alpha", await File.ReadAllTextAsync(kept, Ct));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(docs, "b.txt"), Ct));
        Assert.Equal("made after the backup", await File.ReadAllTextAsync(Path.Combine(docs, "new.txt"), Ct));
        Assert.Equal(new[] { "a.txt", Path.GetFileName(kept), "b.txt", "new.txt" }.Order(StringComparer.Ordinal), Directory.GetFiles(docs).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("restored", run.Message, StringComparison.Ordinal);
        AssertNoStagingLeft(h.Source);
    }

    [Fact]
    public async Task Restoring_everything_over_an_unchanged_folder_changes_nothing()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Restoring_everything_over_an_unchanged_folder_changes_nothing));
        WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await BackUpAsync(h, job);
        var before = Listing(h.Source);

        var run = await RestoreToOriginalAsync(h, job, snapshot, "");

        Assert.Equal(RunState.Succeeded, run.State);
        Assert.Equal(before, Listing(h.Source));
        Assert.Contains("identical", run.Message, StringComparison.Ordinal);
        AssertNoStagingLeft(h.Source);
    }

    [Fact]
    public async Task A_single_deleted_file_comes_back_to_its_place()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_single_deleted_file_comes_back_to_its_place));
        WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await BackUpAsync(h, job);
        File.Delete(Path.Combine(h.Source, "docs", "b.txt"));

        var run = await RestoreToOriginalAsync(h, job, snapshot, "docs/b.txt");

        Assert.Equal(RunState.Succeeded, run.State);
        Assert.Equal(Path.Combine(h.Source, "docs", "b.txt"), run.RestoreTarget);
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(h.Source, "docs", "b.txt"), Ct));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(h.Source, "docs")).Length);
        AssertNoStagingLeft(h.Source);
    }

    [Fact]
    public async Task A_path_that_is_not_in_the_snapshot_is_refused()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_path_that_is_not_in_the_snapshot_is_refused));
        WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await BackUpAsync(h, job);

        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => h.Client.StartRestoreAsync(new StartRestoreRequest
        {
            JobId = job.Id, SnapshotId = snapshot.Id, Path = "docs/nope.txt", Location = RestoreLocation.Original,
        }, cancellationToken: Ct).ResponseAsync);

        Assert.Equal("NotFound", AgentHarness.ErrorKind(ex));
        AssertNoStagingLeft(h.Source);
    }

    internal static void WriteSource(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(root, "docs", "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(root, "top.txt"), "top");
    }

    internal static async Task<Snapshot> BackUpAsync(AgentHarness h, Job job)
    {
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);
        return (await h.Client.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = job.Id }, cancellationToken: Ct)).Snapshots[0];
    }

    private static async Task<Contracts.V1.Run> RestoreToOriginalAsync(AgentHarness h, Job job, Snapshot snapshot, string path)
    {
        var started = await h.Client.StartRestoreAsync(new StartRestoreRequest
        {
            JobId = job.Id, SnapshotId = snapshot.Id, Path = path, Location = RestoreLocation.Original,
        }, cancellationToken: Ct);
        Assert.Equal(RunKind.Restore, started.Kind);
        return (await h.WatchAsync(started.Id)).Finished;
    }

    private static List<string> Listing(string root) =>
        [.. Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => $"{Path.GetRelativePath(root, p)}|{(File.Exists(p) ? File.ReadAllText(p) : "<dir>")}")
            .Order(StringComparer.Ordinal)];

    private static void AssertNoStagingLeft(string root) =>
        Assert.Empty(Directory.GetDirectories(root, ".cashgear-restore-*", SearchOption.AllDirectories));
}
