using System.Diagnostics;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Hardening.Tests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Scale;

/// <summary>
/// Plan, Testing table, Scale row: "1M small files; one 200 GB file. Track duration and RAM per release." Each
/// scenario backs up its source to a local folder through the agent (first backup, then an unchanged one), runs the
/// agent's check, restores into a new folder and byte-compares, recording time and peak memory of every phase in
/// CASHGEAR_SCALE_RESULTS. The defaults are a small variant; docs/hardening.md has the full-run settings.
/// </summary>
[Trait("Category", "Scale")]
public sealed class ScaleTests
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromHours(12);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Many_small_files() => await RunAsync(ScaleSource.ManyFiles(HardeningSettings.ScaleFileCount));

    [Fact(Explicit = true)]
    public async Task One_big_file() => await RunAsync(ScaleSource.BigFile(HardeningSettings.ScaleBigFileBytes));

    /// <summary>Only writes the two sources (kept for the scenarios above), so a long generation can run ahead of the measured runs.</summary>
    [Fact(Explicit = true)]
    public void Generate_sources()
    {
        var many = ScaleSource.ManyFiles(HardeningSettings.ScaleFileCount);
        var big = ScaleSource.BigFile(HardeningSettings.ScaleBigFileBytes);
        Assert.Equal(HardeningSettings.ScaleFileCount, many.Files);
        Assert.Equal(HardeningSettings.ScaleBigFileBytes, big.Bytes);
    }

    private static async Task RunAsync(ScaleSource source)
    {
        EnsureFreeSpace(source);
        using var ws = Workspace.Create(source.Kind, parent: Path.Combine(HardeningSettings.ScaleDirectory, "runs"), source: source.Folder);
        await using var agent = await AgentProcess.StartAsync(ws, Ct);
        await using var monitor = new ResourceMonitor(agent);
        var version = (await agent.Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct)).AgentVersion;
        var destination = await agent.CreateFolderDestinationAsync(ws.Destination, Ct);
        var job = await agent.CreateJobAsync(destination.Id, source.Folder, Ct);
        monitor.EndPhase();
        var results = new List<ScaleResult>();

        ScaleResult Record(string phase, Run run, TimeSpan elapsed, string? note = null)
        {
            var result = new ScaleResult
            {
                Timestamp = DateTimeOffset.Now, Label = HardeningSettings.ScaleLabel, Commit = MachineInfo.Commit, AgentVersion = version,
                KopiaVersion = KopiaTestBinary.Pin.Version, Machine = MachineInfo.Current, Scenario = source.Kind,
                SourceFiles = source.Files, SourceBytes = source.Bytes, Phase = phase, State = run.State.ToString(),
                Seconds = Math.Round(elapsed.TotalSeconds, 1), Files = run.Files, Bytes = run.Bytes, Peaks = monitor.EndPhase(),
                JobPeakCommitSinceStart = agent.Job.PeakCommit().PeakJob, RepositoryBytes = FolderBytes(ws.Destination), Note = note,
            };
            result.Append();
            results.Add(result);
            Log(result.Row());
            return result;
        }

        var first = await TimedAsync(() => agent.BackupAsync(job.Id, PhaseTimeout, Ct));
        Record("first-backup", first.Run, first.Elapsed);
        AssertBackedUp(first.Run, source);

        var unchanged = await TimedAsync(() => agent.BackupAsync(job.Id, PhaseTimeout, Ct));
        Record("unchanged-backup", unchanged.Run, unchanged.Elapsed);
        AssertBackedUp(unchanged.Run, source);

        var settings = await agent.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        var check = await TimedAsync(async () =>
            await agent.WatchToEndAsync((await agent.Client.VerifyJobNowAsync(new VerifyJobNowRequest { JobId = job.Id }, cancellationToken: Ct)).Id, PhaseTimeout, Ct));
        Record("check", check.Run, check.Elapsed, $"{settings.VerifySamplePercent} % of files read back");
        Assert.True(check.Run.State == RunState.Succeeded, $"The check failed: {check.Run.Message}");

        if (HardeningSettings.ScaleRestore)
        {
            var target = ws.RestoreTarget("all");
            var restore = await TimedAsync(() => agent.RestoreAsync(job.Id, first.Run.SnapshotId, target, PhaseTimeout, Ct));
            var compare = Stopwatch.StartNew();
            var differences = Compare(source, target);
            Record("restore", restore.Run, restore.Elapsed, $"byte-compare took {compare.Elapsed.TotalSeconds:N0} s");
            Assert.True(restore.Run.State == RunState.Succeeded, $"The restore failed: {restore.Run.Message}");
            Assert.True(differences.Count == 0, "The restore differs from the source:" + Environment.NewLine + string.Join(Environment.NewLine, differences.Take(20)));
            Workspace.ForceDelete(target);
        }

        Log(Environment.NewLine + ScaleResult.Header + Environment.NewLine + string.Join(Environment.NewLine, results.Select(r => r.Row())));
        Log($"results appended to {HardeningSettings.ScaleResultsFile}");
    }

    private static void AssertBackedUp(Run run, ScaleSource source)
    {
        Assert.True(run.State == RunState.Succeeded, $"The backup ended {run.State}: {run.Message}");
        Assert.Equal(source.Files, run.Files);
        Assert.Equal(source.Bytes, run.Bytes);
    }

    private static List<string> Compare(ScaleSource source, string target)
    {
        if (source.Kind == ScaleSource.BigFileKind)
        {
            Assert.True(source.BigFileUnchanged(), "The big source file changed since it was generated.");
            var restored = Path.Combine(target, ScaleSource.BigFileName);
            var listing = Directory.GetFileSystemEntries(target);
            return listing.Length != 1 || !File.Exists(restored) ? [$"expected only {ScaleSource.BigFileName}, found: {string.Join(", ", listing.Select(Path.GetFileName))}"]
                : SourceData.HashFile(restored) == source.BigFileSha256 ? [] : [$"content differs: {ScaleSource.BigFileName}"];
        }

        return TreeManifest.Capture(source.Folder).ContentDifferences(TreeManifest.Capture(target));
    }

    /// <summary>Fails early, with what is needed, rather than filling the disk part way through.</summary>
    private static void EnsureFreeSpace(ScaleSource source)
    {
        // The repository (incompressible data, so about the source's size) and, when restoring, a full copy.
        var needed = (long)(source.Bytes * (HardeningSettings.ScaleRestore ? 2.1 : 1.1)) + HardeningSettings.GiB;
        var root = Path.GetPathRoot(Path.GetFullPath(HardeningSettings.ScaleDirectory))!;
        var free = new DriveInfo(root).AvailableFreeSpace;
        Assert.True(free >= needed,
            $"{root} has {HardeningSettings.Describe(free)} free; this run needs about {HardeningSettings.Describe(needed)}. " +
            "Point CASHGEAR_SCALE_DIR at a bigger disk, or set CASHGEAR_SCALE_RESTORE=0.");
    }

    private static async Task<(Run Run, TimeSpan Elapsed)> TimedAsync(Func<Task<Run>> phase)
    {
        var stopwatch = Stopwatch.StartNew();
        var run = await phase();
        return (run, stopwatch.Elapsed);
    }

    private static long FolderBytes(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Sum(f => new FileInfo(f).Length);

    /// <summary>To the test output and to CASHGEAR_SCALE_DIR\progress.log, which can be followed during a long run.</summary>
    internal static void Log(string line)
    {
        Chaos.Recovery.Log(line);
        try
        {
            Directory.CreateDirectory(HardeningSettings.ScaleDirectory);
            File.AppendAllText(Path.Combine(HardeningSettings.ScaleDirectory, "progress.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Another writer has the file for a moment; the test output still has the line.
        }
    }
}
