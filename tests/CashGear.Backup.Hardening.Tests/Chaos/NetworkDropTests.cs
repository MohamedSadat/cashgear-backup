using System.Diagnostics;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;
using static CashGear.Backup.Hardening.Tests.Chaos.Recovery;

namespace CashGear.Backup.Hardening.Tests.Chaos;

/// <summary>
/// The network to a remote destination (SFTP, S3) goes down in the middle of a backup. Plan, Testing table, Chaos
/// row: "pull network ... Next run must succeed; repository must verify clean".
/// </summary>
[Trait("Category", "Chaos")]
public sealed class NetworkDropTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The network stays down: the run must not succeed (it fails, or is cancelled once it has run on for a while),
    /// and once the network is back the next run succeeds.
    /// </summary>
    [Theory(Explicit = true)]
    [InlineData(RemoteDestination.Sftp)]
    [InlineData(RemoteDestination.S3)]
    public async Task Network_down_mid_backup_never_succeeds_and_the_next_run_does(string kind)
    {
        using var ws = Workspace.Create($"network-down-{kind}");
        await using var remote = await RemoteDestination.StartAsync(kind, ws.Root, Ct);
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        var (destination, job, before) = await SetUpAsync(agent, remote);

        var run = await StartBulkBackupAsync(agent, job, remote.Proxy);
        remote.Proxy.Cut();
        var sinceCut = Stopwatch.StartNew();
        Log($"network cut at {remote.Proxy.BytesToServer:N0} bytes sent");

        var faulted = await EndFaultedRunAsync(agent, run.Id, sinceCut);
        AssertNotSucceeded(faulted);

        remote.Proxy.Restore();
        await AssertRecoversAsync(agent, job, destination, [before], remote.StorageEnvironment);
    }

    /// <summary>
    /// The network drops and comes back while the run goes on: a short drop that Kopia's retries ride out, and an
    /// outage longer than its retries (about 11 s per write). Either way nothing damaged may be recorded as a good
    /// restore point: a run that says Succeeded must restore identically, and anything else must not say Succeeded.
    /// </summary>
    [Theory(Explicit = true)]
    [InlineData(RemoteDestination.Sftp, 5)]
    [InlineData(RemoteDestination.S3, 5)]
    [InlineData(RemoteDestination.Sftp, 60)]
    [InlineData(RemoteDestination.S3, 60)]
    public async Task Network_back_after_an_outage_mid_backup_never_leaves_a_bad_restore_point(string kind, int outageSeconds)
    {
        using var ws = Workspace.Create($"network-outage-{kind}-{outageSeconds}s");
        await using var remote = await RemoteDestination.StartAsync(kind, ws.Root, Ct);
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        var (destination, job, before) = await SetUpAsync(agent, remote);

        var run = await StartBulkBackupAsync(agent, job, remote.Proxy);
        remote.Proxy.Cut();
        Log($"network cut at {remote.Proxy.BytesToServer:N0} bytes sent; back in {outageSeconds} s");
        await Task.Delay(TimeSpan.FromSeconds(outageSeconds), Ct);
        remote.Proxy.Restore();

        var outcome = await agent.WatchToEndAsync(run.Id, RunTimeout, Ct);
        Log($"the run went on to: {outcome.State}, snapshot '{outcome.SnapshotId}', {outcome.FailedItemCount} failed item(s). {outcome.Message}");
        List<RestorePoint> earlier = [before];
        if (outcome.State == RunState.Succeeded)
        {
            // It rode out the drop: then it must be a whole restore point, restoring every file byte for byte.
            Assert.Equal(0, outcome.FailedItemCount);
            earlier.Add(new RestorePoint(outcome.SnapshotId, TreeManifest.Capture(agent.Workspace.Source)));
        }
        else
        {
            AssertNotSucceeded(outcome);
        }

        await AssertRecoversAsync(agent, job, destination, earlier, remote.StorageEnvironment);
    }

    internal static async Task<(Destination Destination, Job Job, RestorePoint Before)> SetUpAsync(AgentProcess agent, RemoteDestination remote)
    {
        var destination = await agent.CreateDestinationAsync(remote.Request(), Ct);
        var (_, job, before) = await AgentCrashTests.SetUpWithRestorePointAsync(agent, destination);
        return (destination, job, before);
    }

    /// <summary>Adds the bulk data and follows its backup until a quarter of it has gone through the proxy.</summary>
    internal static async Task<Run> StartBulkBackupAsync(AgentProcess agent, Job job, ChaosProxy proxy)
    {
        SourceData.WriteRandomFiles(Path.Combine(agent.Workspace.Source, "bulk"), HardeningSettings.ChaosSourceBytes);
        var sentBefore = proxy.BytesToServer;
        var run = await agent.StartBackupAsync(job.Id, Ct);
        var (_, finished) = await agent.WatchUntilAsync(run.Id, _ => proxy.BytesToServer - sentBefore >= HardeningSettings.ChaosSourceBytes / 4, RunTimeout, Ct);
        AssertStillRunning(finished, SizeHint);
        return run;
    }
}
