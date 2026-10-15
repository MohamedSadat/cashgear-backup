using System.Text;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Engine.Kopia.Policy;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>The release gate (plan, "Testing"): back up, change files, back up again, restore, compare every byte.</summary>
[Trait("Category", "Integration")]
public class EngineRoundTripTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_and_incremental_backups_restore_byte_identical_latest_and_point_in_time()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Full_and_incremental_backups_restore_byte_identical_latest_and_point_in_time));
        TreeFixture.Create(h.Workspace.Source);
        var manifest1 = TreeManifest.Capture(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);

        var progress1 = new List<RunProgress>();
        var run1 = await h.Engine.StartBackupAsync(job, Ct);
        Assert.Equal(RunKind.Backup, run1.Kind);
        Assert.Equal(job.Id, run1.JobId);
        var final1 = await h.RunToEndAsync(run1, progress1);
        var backup1 = final1.Result!;
        Assert.Equal(RunState.Succeeded, backup1.State);
        Assert.NotNull(backup1.SnapshotId);
        Assert.Equal(manifest1.FileCount, backup1.Files);
        Assert.Equal(manifest1.TotalBytes, backup1.Bytes);
        Assert.Equal(0, backup1.FailedItemCount);
        Assert.Equal(manifest1.FileCount, final1.FilesProcessed);
        Assert.Equal(RunState.Running, progress1[0].State);
        Assert.True(progress1.Zip(progress1.Skip(1)).All(p => p.Second.FilesProcessed >= p.First.FilesProcessed), "progress went backwards");

        TreeFixture.Mutate(h.Workspace.Source);
        var manifest2 = TreeManifest.Capture(h.Workspace.Source);
        var final2 = await h.RunToEndAsync(await h.Engine.StartBackupAsync(job, Ct));
        var backup2 = final2.Result!;
        Assert.Equal(RunState.Succeeded, backup2.State);
        Assert.NotEqual(backup1.SnapshotId, backup2.SnapshotId);
        Assert.True(final2.FilesHashed < final2.FilesProcessed, $"incremental run re-read {final2.FilesHashed} of {final2.FilesProcessed} files");

        var snapshots = await h.Engine.ListSnapshotsAsync(job, Ct);
        Assert.Equal([backup2.SnapshotId, backup1.SnapshotId], snapshots.Select(s => s.Id));
        Assert.All(snapshots, s => Assert.True(s.IsComplete));
        Assert.All(snapshots, s => Assert.Equal(h.RepositoryId, s.RepositoryId));
        Assert.Equal(manifest2.FileCount, snapshots[0].Files);

        var latest = h.Workspace.RestoreTarget("latest");
        var restoreLatest = await h.RestoreAsync(snapshots[0], latest);
        Assert.Equal(RunState.Succeeded, restoreLatest.State);
        Assert.Equal(manifest2.FileCount, restoreLatest.Files);
        EngineHarness.AssertIdentical(manifest2, latest);

        var pointInTime = Path.Combine(h.Workspace.RestoreTarget("point-in-time"), "2026-10-15", "documents");
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshots[1], pointInTime)).State);
        EngineHarness.AssertIdentical(manifest1, pointInTime);

        EngineHarness.AssertNoPlaceholders(h.Workspace.Restores);
    }

    [Fact]
    public async Task A_subfolder_and_a_single_file_restore_into_an_empty_folder()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_subfolder_and_a_single_file_restore_into_an_empty_folder));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];

        var folder = h.Workspace.RestoreTarget("nested");
        Directory.CreateDirectory(folder); // an existing empty folder is a valid target
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, folder, "nested")).State);
        EngineHarness.AssertIdentical(TreeManifest.Capture(Path.Combine(h.Workspace.Source, "nested")), folder);

        var file = Path.Combine(h.Workspace.RestoreTarget("parents-not-created-yet"), "one-file");
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, file, TreeFixture.DeepFile)).State);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(h.Workspace.Source, TreeFixture.DeepFile), Ct),
            await File.ReadAllBytesAsync(Path.Combine(file, Path.GetFileName(TreeFixture.DeepFile)), Ct));
        Assert.Single(Directory.GetFileSystemEntries(file));

        EngineHarness.AssertNoPlaceholders(h.Workspace.Restores);
    }

    [Fact]
    public async Task Browse_lists_folders_and_files_of_a_snapshot()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Browse_lists_folders_and_files_of_a_snapshot));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        await h.BackupAsync(job);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];

        var root = await h.Engine.BrowseAsync(snapshot, "", Ct);
        Assert.Equal(SnapshotEntryKind.Directory, root.Single(e => e.Name == "many").Kind);
        var empty = root.Single(e => e.Name == "empty.txt");
        Assert.Equal((SnapshotEntryKind.File, 0L, "empty.txt"), (empty.Kind, empty.Size, empty.Path));
        Assert.Equal(new FileInfo(Path.Combine(h.Workspace.Source, "empty.txt")).LastWriteTimeUtc, empty.ModifiedAt!.Value.UtcDateTime);

        var many = await h.Engine.BrowseAsync(snapshot, "many", Ct);
        Assert.Equal(200, many.Count);
        var file = many.Single(e => e.Name == "file-042.txt");
        Assert.Equal("many/file-042.txt", file.Path);
        Assert.Equal(new FileInfo(Path.Combine(h.Workspace.Source, "many", "file-042.txt")).Length, file.Size);

        var deep = await h.Engine.BrowseAsync(snapshot, @"nested\a/b", Ct);
        Assert.Equal("nested/a/b/c", Assert.Single(deep).Path);

        foreach (var bad in new[] { "empty.txt", "no-such-folder", "../x" })
        {
            var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.BrowseAsync(snapshot, bad, Ct));
            Assert.Equal(BackupErrorKind.InvalidRequest, ex.Kind);
        }
    }

    [Fact]
    public async Task Snapshots_survive_an_agent_restart_and_backups_continue()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Snapshots_survive_an_agent_restart_and_backups_continue));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        await h.BackupAsync(job);
        var before = (await h.Engine.ListSnapshotsAsync(job, Ct)).Select(s => s.Id).ToList();
        await h.Engine.DisposeAsync();

        // Same data folder: the config exists, so the server opens the repository at start with KOPIA_PASSWORD.
        var engine = h.NewEngine();
        h.UseEngine(engine);
        var info = await engine.ConnectRepositoryAsync(h.Destination, h.Password, Ct);
        Assert.Equal(h.RepositoryId, info.RepositoryId);
        Assert.Equal($"cashgear@{EngineHarness.ClientHost}", info.ClientIdentity);
        Assert.Equal(before, (await engine.ListSnapshotsAsync(job, Ct)).Select(s => s.Id));

        // Connecting again is a no-op that reports the same repository.
        Assert.Equal(info, await engine.ConnectRepositoryAsync(h.Destination, h.Password, Ct));

        File.WriteAllText(Path.Combine(h.Workspace.Source, "after-restart.txt"), "new");
        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        Assert.Equal(2, (await engine.ListSnapshotsAsync(job, Ct)).Count);
    }

    [Fact]
    public async Task A_new_data_folder_connects_to_an_existing_repository()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_new_data_folder_connects_to_an_existing_repository));
        TreeFixture.Create(h.Workspace.Source);
        var manifest = TreeManifest.Capture(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        var backup = await h.BackupAsync(job);
        await h.Engine.DisposeAsync();

        // No local config (a reinstall, or the first use on this machine): repo/connect with the password.
        var engine = h.NewEngine(dataDirectory: Path.Combine(h.Workspace.Root, "data-reinstalled"));
        h.UseEngine(engine);
        await engine.ConnectRepositoryAsync(h.Destination, h.Password, Ct);
        var snapshot = Assert.Single(await engine.ListSnapshotsAsync(job, Ct));
        Assert.Equal(backup.SnapshotId, snapshot.Id);

        var target = h.Workspace.RestoreTarget("reinstalled");
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, target)).State);
        EngineHarness.AssertIdentical(manifest, target);
    }

    [Fact]
    public async Task The_job_policy_is_written_whole_verified_and_takes_effect()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(The_job_policy_is_written_whole_verified_and_takes_effect));
        var source = h.Workspace.Source;
        Directory.CreateDirectory(Path.Combine(source, "cache"));
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(source, "scratch.tmp"), "excluded by pattern");
        File.WriteAllText(Path.Combine(source, "cache", "blob.bin"), "excluded by folder");
        File.WriteAllText(Path.Combine(source, ".kopiaignore"), "keep.txt\n"); // must NOT be honoured
        var text = new StringBuilder();
        for (var i = 0; text.Length < 8 * 1024 * 1024; i++)
        {
            text.Append("line ").Append(i).Append(": the quick brown fox jumps over the lazy dog\n");
        }

        File.WriteAllText(Path.Combine(source, "compressible.txt"), text.ToString());

        var job = h.Job() with
        {
            Excludes = ["*.tmp", "/cache/"],
            Compression = BackupCompression.Balanced,
            Retention = new RetentionRule { KeepLatest = 5, KeepHourly = 1, KeepDaily = 2, KeepWeekly = 3, KeepMonthly = 4, KeepAnnual = 6 },
        };
        await h.Engine.ApplyJobPolicyAsync(job, Ct);

        var client = await h.ClientAsync();
        var stored = await client.GetPolicyAsync(h.Engine.SourceFor(job), Ct);
        Assert.Empty(KopiaPolicyMapper.Differences(KopiaPolicyMapper.ToPolicy(job), stored));
        var effective = (await client.ResolvePolicyAsync(h.Engine.SourceFor(job), new Api.ResolvePolicyRequest(stored!.Value), Ct)).Effective;
        Assert.Equal(5, JsonShape.Get(effective, "retention.keepLatest").GetInt32());
        Assert.Equal(1, JsonShape.Get(effective, "retention.keepHourly").GetInt32());
        Assert.Equal(6, JsonShape.Get(effective, "retention.keepAnnual").GetInt32());

        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];
        var names = (await h.Engine.BrowseAsync(snapshot, "", Ct)).Select(e => e.Name).ToHashSet();
        Assert.Contains("keep.txt", names);
        Assert.Contains("compressible.txt", names);
        Assert.DoesNotContain("scratch.tmp", names);
        Assert.DoesNotContain("cache", names);

        var repositoryBytes = Directory.EnumerateFiles(h.Workspace.Destination, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(repositoryBytes < 4 * 1024 * 1024, $"repository is {repositoryBytes} bytes for 8 MiB of text: compression is not on");
    }

    [Fact]
    public async Task Policy_drift_is_repaired_before_the_next_backup()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Policy_drift_is_repaired_before_the_next_backup));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "a.txt"), "a");
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);

        // Someone (or a Kopia upgrade) leaves a partial policy: Kopia's default retention would prune.
        var client = await h.ClientAsync();
        var source = h.Engine.SourceFor(job);
        var (status, _) = await client.SendRawAsync(Api.KopiaUser.Ui, HttpMethod.Put, "/api/v1/policy?" + source.ToQuery(), """{"retention":{"keepLatest":1}}""", includeCsrf: true, Ct);
        Assert.Equal(200, status);
        Assert.NotEmpty(KopiaPolicyMapper.Differences(KopiaPolicyMapper.ToPolicy(job), await client.GetPolicyAsync(source, Ct)));

        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        Assert.Empty(KopiaPolicyMapper.Differences(KopiaPolicyMapper.ToPolicy(job), await client.GetPolicyAsync(source, Ct)));
    }

    [Fact]
    public async Task StartBackup_without_ApplyJobPolicy_still_runs_under_the_job_policy()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(StartBackup_without_ApplyJobPolicy_still_runs_under_the_job_policy));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "a.txt"), "a");
        var job = h.Job();

        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        var client = await h.ClientAsync();
        Assert.Empty(KopiaPolicyMapper.Differences(KopiaPolicyMapper.ToPolicy(job), await client.GetPolicyAsync(h.Engine.SourceFor(job), Ct)));
    }
}
