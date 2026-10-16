using System.Diagnostics;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;
using Grpc.Core;

namespace CashGear.Backup.Hardening.Tests.Chaos;

/// <summary>A snapshot and the manifest of the source it was taken from: it must restore byte for byte.</summary>
internal sealed record RestorePoint(string SnapshotId, TreeManifest Manifest);

/// <summary>
/// What every chaos test asserts once its fault has passed (plan, Testing table, Chaos row): the faulted run was
/// not recorded as Succeeded (CLAUDE.md rule 5), the next run succeeds, and the repository verifies clean, both by
/// the agent's own check and by an independent kopia consistency check. Restores of the new restore point and of
/// the one taken before the fault are byte-compared with their sources.
/// </summary>
internal static class Recovery
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

    /// <summary>The fault has to land while the run is still going; a run that already ended proves nothing.</summary>
    public static void AssertStillRunning(Run? finished, string hint)
    {
        Assert.True(finished is null,
            $"The backup ended ({finished?.State}: {finished?.Message}) before the fault could be injected, so this run proves nothing. {hint}");
    }

    public static string SizeHint => $"Raise CASHGEAR_CHAOS_SOURCE_MB (now {HardeningSettings.ChaosSourceBytes / HardeningSettings.MiB}) so the backup lasts longer.";

    /// <summary>Rule 5: a run cut short by a fault is never Succeeded and records no restore point.</summary>
    public static void AssertNotSucceeded(Run run)
    {
        Log($"faulted run: {run.State}, snapshot '{run.SnapshotId}', message: {run.Message}");
        Assert.NotEqual(RunState.Succeeded, run.State);
        Assert.NotEqual(RunState.Running, run.State);
        if (run.State != RunState.CompletedWithWarnings)
        {
            Assert.True(string.IsNullOrEmpty(run.SnapshotId), $"run {run.Id} is {run.State} but recorded snapshot {run.SnapshotId}");
        }
    }

    /// <summary>
    /// A run whose destination failed under it must end by itself (the engine ends it once Kopia's writes have failed
    /// for 90 s; docs/hardening.md, finding 3) within <see cref="HardeningSettings.ChaosFaultWait"/> of the fault. If it
    /// has not, the test cancels it through the agent, as a user would, and fails.
    /// </summary>
    public static async Task<Run> EndFaultedRunAsync(AgentProcess agent, string runId, Stopwatch sinceFault)
    {
        var wait = HardeningSettings.ChaosFaultWait;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            limit.CancelAfter(wait > sinceFault.Elapsed ? wait - sinceFault.Elapsed : TimeSpan.Zero);
            try
            {
                var ended = await agent.WatchToEndAsync(runId, RunTimeout, limit.Token);
                Log($"the run ended by itself {sinceFault.Elapsed.TotalSeconds:N0} s after the fault: {ended.State}");
                return ended;
            }
            catch (Exception ex) when (ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && !Ct.IsCancellationRequested)
            {
            }
        }

        var progress = (await agent.WatchUntilAsync(runId, _ => true, TimeSpan.FromMinutes(1), Ct)).Matched;
        Log($"still running {sinceFault.Elapsed.TotalSeconds:N0} s after the fault ({progress?.ErrorCount} errors so far, {progress?.FilesProcessed} files processed); cancelling it");
        var stopwatch = Stopwatch.StartNew();
        await agent.Client.CancelRunAsync(new CancelRunRequest { RunId = runId }, cancellationToken: Ct);
        var canceled = await agent.WatchToEndAsync(runId, RunTimeout, Ct);
        Log($"the cancel took {stopwatch.Elapsed.TotalSeconds:N1} s: {canceled.State}");
        Assert.Fail($"The run whose destination was lost had not ended by itself {wait.TotalSeconds:N0} s after the fault; it was cancelled ({canceled.State}).");
        return canceled;
    }

    /// <summary>The engine's message for a run it ended because the destination went away (Runs/DestinationWatch).</summary>
    public static void AssertLostDestination(Run run, string expectedStart)
    {
        Assert.Equal(RunState.Failed, run.State);
        Assert.StartsWith(expectedStart, run.Message, StringComparison.Ordinal);
    }

    /// <summary>Polls until <paramref name="condition"/> holds.</summary>
    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < timeout, $"Timed out after {timeout} waiting for {what}.");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>
    /// The next run succeeds; the agent's check (every file read back) and the independent kopia check pass; the new
    /// restore point and the <paramref name="earlier"/> ones restore identical to their sources; and every Succeeded
    /// backup in the job's history is a complete snapshot. Kills the agent at the end (idle) for the independent check.
    /// </summary>
    public static async Task AssertRecoversAsync(AgentProcess agent, Job job, Destination destination, IReadOnlyList<RestorePoint> earlier,
        IReadOnlyDictionary<string, string>? storageEnvironment = null)
    {
        var next = await agent.BackupAsync(job.Id, RunTimeout, Ct);
        Log($"next run: {next.State}, snapshot {next.SnapshotId}, {next.Files} files, {next.Bytes} bytes. {next.Message}");
        Assert.True(next.State == RunState.Succeeded, $"The run after the fault must succeed; it was {next.State}: {next.Message}");
        Assert.False(string.IsNullOrEmpty(next.SnapshotId));
        Assert.Equal(0, next.FailedItemCount);
        var after = new RestorePoint(next.SnapshotId, TreeManifest.Capture(job.Spec.SourcePath));

        var check = await agent.VerifyAllAsync(job.Id, RunTimeout, Ct);
        Log($"agent check: {check.State}, read back {check.Files} files / {check.Bytes} bytes. {check.Message}");
        Assert.True(check.State == RunState.Succeeded, $"The agent's check failed: {check.Message} {string.Join("; ", check.FailedItems.Select(f => f.Error))}");

        foreach (var (name, point) in earlier.Select((p, i) => ($"earlier-{i + 1}", p)).Prepend(("after", after)))
        {
            var target = agent.Workspace.RestoreTarget(name);
            var restore = await agent.RestoreAsync(job.Id, point.SnapshotId, target, RunTimeout, Ct);
            Assert.True(restore.State == RunState.Succeeded, $"Restoring the {name} snapshot failed: {restore.Message}");
            var differences = point.Manifest.ContentDifferences(TreeManifest.Capture(target));
            Assert.True(differences.Count == 0, $"The {name} snapshot did not restore identically:{Environment.NewLine}{string.Join(Environment.NewLine, differences.Take(20))}");
            Log($"restore of the {name} snapshot: {point.Manifest.FileCount} files identical");
        }

        var snapshots = await agent.ListSnapshotsAsync(job.Id, Ct);
        foreach (var run in (await agent.ListRunsAsync(job.Id, Ct)).Where(r => r.Kind == RunKind.Backup))
        {
            Assert.NotEqual(RunState.Running, run.State);
            if (run.State == RunState.Succeeded)
            {
                var snapshot = snapshots.SingleOrDefault(s => s.Id == run.SnapshotId);
                Assert.True(snapshot is { Complete: true, FailedItemCount: 0 }, $"Succeeded run {run.Id} points at {run.SnapshotId}, which is not a complete snapshot.");
            }
        }

        await agent.KillAsync();
        var result = await RepositoryCheck.RunAsync(agent, destination.Id, Ct, storageEnvironment);
        Log("independent repository check:" + Environment.NewLine + result.Report);
        Assert.True(result.Clean, "The repository does not verify clean:" + Environment.NewLine + result.Report);
        Assert.True(result.Snapshots >= earlier.Count + 1, $"Expected the restore points in the repository; kopia lists {result.Snapshots}.");
    }
}
