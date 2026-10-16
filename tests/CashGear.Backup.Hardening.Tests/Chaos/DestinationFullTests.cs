using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;
using static CashGear.Backup.Hardening.Tests.Chaos.Recovery;

namespace CashGear.Backup.Hardening.Tests.Chaos;

/// <summary>
/// The destination runs out of space in the middle of a backup. Plan, Testing table, Chaos row: "fill destination
/// disk ... Next run must succeed; repository must verify clean".
/// </summary>
[Trait("Category", "Chaos")]
public sealed class DestinationFullTests
{
    /// <summary>Room for the first restore point, with a margin.</summary>
    private const long FirstRestorePointRoom = 64 * HardeningSettings.MiB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A NAS reached over SFTP whose space (the user's quota) runs out; then the owner frees space.</summary>
    [Fact(Explicit = true)]
    public async Task A_full_sftp_destination_fails_the_run_and_once_space_is_freed_the_next_run_succeeds()
    {
        using var ws = Workspace.Create("destination-full-sftp");
        var quota = FirstRestorePointRoom + (HardeningSettings.ChaosSourceBytes / 2);
        await using var nas = await QuotaSftpServer.StartAsync(ws.Root, quota, Ct);
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        var destination = await agent.CreateDestinationAsync(RemoteDestination.SftpRequest(nas.Username, nas.Password, nas.KnownHosts, nas.Proxy), Ct);
        var (_, job, before) = await AgentCrashTests.SetUpWithRestorePointAsync(agent, destination);

        Log($"quota {HardeningSettings.Describe(quota)}; backing up {HardeningSettings.Describe(HardeningSettings.ChaosSourceBytes)} more");
        // Full once less than a pack (about 20 MB) of the quota is left: from then on SFTPGo refuses the writes.
        var full = await BackUpUntilFullAsync(agent, job, () => nas.UsedBytes() > quota - (32 * HardeningSettings.MiB));
        AssertNotSucceeded(full);

        await nas.RestartAsync(quotaBytes: 0, Ct);
        await AssertRecoversAsync(agent, job, destination, [before]);
    }

    /// <summary>
    /// A local backup disk (a small VHDX volume) fills up; then the user deletes other files on it. Needs an elevated
    /// run to attach the volume.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task A_full_local_disk_fails_the_run_and_once_space_is_freed_the_next_run_succeeds()
    {
        Assert.SkipUnless(SmallVolume.CanCreate, "Needs an elevated (administrator) test run: it attaches a small VHDX as the backup disk.");
        using var ws = Workspace.Create("destination-full-disk");
        await using var volume = await SmallVolume.CreateAsync(ws.Root, (2 * HardeningSettings.ChaosSourceBytes) + (256 * HardeningSettings.MiB), Ct);
        await using var agent = await AgentProcess.StartAsync(ws, Ct);

        // Other files on the disk leave room for the first restore point and about half of the new data.
        var filler = Path.Combine(volume.MountPoint, "other-files.bin");
        SourceData.WriteRandomFile(filler, volume.FreeBytes - FirstRestorePointRoom - (HardeningSettings.ChaosSourceBytes / 2));
        var destination = await agent.CreateFolderDestinationAsync(Path.Combine(volume.MountPoint, "CashGear Backup"), Ct);
        var (_, job, before) = await AgentCrashTests.SetUpWithRestorePointAsync(agent, destination);

        Log($"{HardeningSettings.Describe(volume.FreeBytes)} free; backing up {HardeningSettings.Describe(HardeningSettings.ChaosSourceBytes)} more");
        var full = await BackUpUntilFullAsync(agent, job, () => volume.FreeBytes < 16 * HardeningSettings.MiB);
        Log($"{HardeningSettings.Describe(volume.FreeBytes)} free after the run");
        AssertNotSucceeded(full);

        File.Delete(filler);
        await AssertRecoversAsync(agent, job, destination, [before]);
    }

    /// <summary>Backs up the bulk data until the destination is full, then lets the run end (or cancels it).</summary>
    private static async Task<Run> BackUpUntilFullAsync(AgentProcess agent, Job job, Func<bool> isFull)
    {
        SourceData.WriteRandomFiles(Path.Combine(agent.Workspace.Source, "bulk"), HardeningSettings.ChaosSourceBytes);
        var run = await agent.StartBackupAsync(job.Id, Ct);
        var (_, finished) = await agent.WatchUntilAsync(run.Id, _ => isFull(), RunTimeout, Ct);
        if (finished is not null)
        {
            Assert.True(isFull(), $"The backup ended ({finished.State}) without filling the destination.");
            return finished;
        }

        Log("the destination is full");
        return await EndFaultedRunAsync(agent, run.Id, System.Diagnostics.Stopwatch.StartNew());
    }
}
