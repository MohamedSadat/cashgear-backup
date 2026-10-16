using System.Diagnostics;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>
/// The kopia server dies in the middle of a backup (docs/hardening.md, finding 2): the run is lost with it and must say
/// so, and the engine must replace the server as soon as its process ends rather than at the next health check.
/// </summary>
[Trait("Category", "Integration")]
public class EngineRestartTests
{
    private static CancellationToken Ct => EngineHarness.Ct;

    [Fact]
    public async Task A_server_that_dies_mid_backup_fails_the_run_as_an_engine_restart_and_is_replaced_at_once()
    {
        // The health check is far away: only the process exit can bring the new server in time.
        await using var h = await EngineHarness.CreateAsync(nameof(A_server_that_dies_mid_backup_fails_the_run_as_an_engine_restart_and_is_replaced_at_once),
            configure: o => o with { HealthCheckInterval = TimeSpan.FromMinutes(10) });
        var random = new Random(5);
        var bytes = new byte[8 * 1024 * 1024];
        for (var i = 0; i < 4; i++)
        {
            random.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(h.Workspace.Source, $"random-{i}.bin"), bytes, Ct);
        }

        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        var client = await h.ClientAsync();
        await client.SetThrottleAsync(new ThrottleLimits { MaxUploadSpeedBytesPerSecond = 4_000_000 }, Ct);
        var run = await h.Engine.StartBackupAsync(job, Ct);
        await foreach (var progress in h.Engine.WatchAsync(run, Ct))
        {
            Assert.Null(progress.Result);
            if (progress.BytesHashed > 0)
            {
                break;
            }
        }

        var host = h.Engine.HostFor(h.RepositoryId)!;
        var dead = host.CurrentProcess!;
        var deadPid = dead.ProcessId;
        dead.Process.Kill();
        var sinceKill = Stopwatch.StartNew();
        var result = (await h.RunToEndAsync(run)).Result!;

        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.SnapshotId);
        Assert.StartsWith("The backup engine stopped during the run (its process ended unexpectedly)", result.Message, StringComparison.Ordinal);
        Assert.True(sinceKill.Elapsed < TimeSpan.FromSeconds(30), $"the run took {sinceKill.Elapsed} to end after the server died");
        Assert.False(h.Log.Contains("Following run"), "the lost run was logged as a failure to follow it");
        Assert.DoesNotContain(h.Log.AllText.Split(Environment.NewLine), line => line.Contains(" Error ", StringComparison.Ordinal) && line.Contains("KopiaBackupEngine", StringComparison.Ordinal));

        // A new server, long before the health check would have looked.
        var stopwatch = Stopwatch.StartNew();
        while (host.CurrentProcess is not { } current || current.ProcessId == deadPid)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "the dead server was not replaced");
            await Task.Delay(100, Ct);
        }

        await (await h.ClientAsync()).SetThrottleAsync(new ThrottleLimits(), Ct);
        var next = await h.BackupAsync(job);
        Assert.True(next.State == RunState.Succeeded, $"the run after the restart was {next.State}: {next.Message}");
    }
}
