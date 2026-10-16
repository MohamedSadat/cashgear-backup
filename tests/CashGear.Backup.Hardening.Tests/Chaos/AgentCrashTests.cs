using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;
using static CashGear.Backup.Hardening.Tests.Chaos.Recovery;

namespace CashGear.Backup.Hardening.Tests.Chaos;

/// <summary>
/// The agent, or the kopia server it runs, dies in the middle of a backup to a local folder. Plan, Testing table,
/// Chaos row: "Kill agent mid-backup ... Next run must succeed; repository must verify clean".
/// </summary>
[Trait("Category", "Chaos")]
public sealed class AgentCrashTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Agent_killed_mid_backup_takes_kopia_down_and_the_next_run_succeeds()
    {
        using var ws = Workspace.Create(nameof(Agent_killed_mid_backup_takes_kopia_down_and_the_next_run_succeeds));
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        var (destination, job, before) = await SetUpWithRestorePointAsync(agent);

        SourceData.WriteRandomFiles(Path.Combine(ws.Source, "bulk"), HardeningSettings.ChaosSourceBytes);
        var run = await agent.StartBackupAsync(job.Id, Ct);
        var (progress, finished) = await agent.WatchUntilAsync(run.Id, p => p.BytesHashed >= HardeningSettings.ChaosSourceBytes / 4, RunTimeout, Ct);
        AssertStillRunning(finished, SizeHint);
        Log($"killing the agent at {progress!.BytesHashed:N0} of {HardeningSettings.ChaosSourceBytes:N0} bytes hashed");

        var kopia = await agent.KillAsync();
        Assert.NotEmpty(kopia);
        await WaitForAsync(() => kopia.All(p => p.HasExited), TimeSpan.FromSeconds(15),
            "the kopia server to exit with the agent (the engine's kill-on-close Job Object)");

        await agent.StartAsync(Ct);
        var interrupted = (await agent.ListRunsAsync(job.Id, Ct)).Single(r => r.Id == run.Id);
        AssertNotSucceeded(interrupted);
        Assert.Equal(RunState.Failed, interrupted.State);
        Assert.Contains("agent stopped during this run", interrupted.Message, StringComparison.Ordinal);

        await AssertRecoversAsync(agent, job, destination, [before]);
    }

    [Fact(Explicit = true)]
    public async Task Kopia_server_killed_mid_backup_fails_the_run_and_the_next_run_succeeds()
    {
        using var ws = Workspace.Create(nameof(Kopia_server_killed_mid_backup_fails_the_run_and_the_next_run_succeeds));
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        var (destination, job, before) = await SetUpWithRestorePointAsync(agent);

        SourceData.WriteRandomFiles(Path.Combine(ws.Source, "bulk"), HardeningSettings.ChaosSourceBytes);
        var run = await agent.StartBackupAsync(job.Id, Ct);
        var (progress, finished) = await agent.WatchUntilAsync(run.Id, p => p.BytesHashed >= HardeningSettings.ChaosSourceBytes / 4, RunTimeout, Ct);
        AssertStillRunning(finished, SizeHint);

        var server = Assert.Single(agent.Processes(AgentProcess.KopiaProcessName));
        Log($"killing kopia server {server.Id} at {progress!.BytesHashed:N0} bytes hashed");
        server.Kill();
        await server.WaitForExitAsync(Ct);

        var sinceKill = System.Diagnostics.Stopwatch.StartNew();
        var faulted = await agent.WatchToEndAsync(run.Id, RunTimeout, Ct);
        Log($"the run ended {sinceKill.Elapsed.TotalSeconds:N1} s after the kill");
        AssertNotSucceeded(faulted);
        Assert.Equal(RunState.Failed, faulted.State);
        Assert.StartsWith("The backup engine stopped during the run (its process ended unexpectedly)", faulted.Message, StringComparison.Ordinal);

        // The engine starts a new server as soon as the old one's process ends (finding 2), not at its next health check (30 s).
        await WaitForAsync(() => agent.Processes(AgentProcess.KopiaProcessName).Any(p => p.Id != server.Id), TimeSpan.FromSeconds(20),
            "the engine to start a new kopia server");
        await AssertRecoversAsync(agent, job, destination, [before]);
    }

    /// <summary>A folder destination and a job with one good restore point, which the fault must not damage.</summary>
    internal static async Task<(Destination Destination, Job Job, RestorePoint Before)> SetUpWithRestorePointAsync(AgentProcess agent, Destination? destination = null)
    {
        TreeFixture.Create(agent.Workspace.Source);
        destination ??= await agent.CreateFolderDestinationAsync(agent.Workspace.Destination, Ct);
        var job = await agent.CreateJobAsync(destination.Id, agent.Workspace.Source, Ct);
        var first = await agent.BackupAsync(job.Id, RunTimeout, Ct);
        Assert.True(first.State == RunState.Succeeded, $"The first backup failed: {first.Message}");
        return (destination, job, new RestorePoint(first.SnapshotId, TreeManifest.Capture(agent.Workspace.Source)));
    }
}
