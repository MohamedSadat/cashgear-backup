using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>
/// Verification through <see cref="IBackupEngine.VerifyAsync"/>: Kopia's consistency check plus a sample of file
/// contents read back from the destination. Observed: reads served from Kopia's local content cache are not checked
/// against the destination, so damage only shows when the check reads through an empty cache (FINDINGS, Phase 3).
/// </summary>
[Trait("Category", "Integration")]
public class VerifyTests
{
    private static CancellationToken Ct => EngineHarness.Ct;

    [Fact]
    public async Task A_healthy_backup_passes_and_every_file_is_read_back_at_100_percent()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_healthy_backup_passes_and_every_file_is_read_back_at_100_percent));
        TreeFixture.Create(h.Workspace.Source);
        var manifest = TreeManifest.Capture(h.Workspace.Source);
        var job = h.Job();
        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);

        var result = await h.Engine.VerifyAsync(job, 1.0, Ct);

        Assert.True(result.Passed, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal(manifest.FileCount, result.FilesRead);
        Assert.Equal(manifest.TotalBytes, result.BytesRead);
        Assert.True(result.ItemsChecked >= manifest.FileCount);
    }

    [Fact]
    public async Task Zero_percent_checks_structure_without_reading_files()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Zero_percent_checks_structure_without_reading_files));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.BackupAsync(job);

        var result = await h.Engine.VerifyAsync(job, 0, Ct);

        Assert.True(result.Passed);
        Assert.Equal(0, result.FilesRead);
        Assert.True(result.ItemsChecked > 0);
    }

    [Fact]
    public async Task Damaged_data_at_the_destination_is_found_even_after_the_server_read_it()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Damaged_data_at_the_destination_is_found_even_after_the_server_read_it));
        WriteRandomFiles(h.Workspace.Source, count: 12, size: 64 * 1024);
        var job = h.Job();
        await h.BackupAsync(job);
        // A restore makes the server read (and cache) every content first.
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, h.Workspace.RestoreTarget("warm-cache"))).State);

        CorruptDataPacks(h.Workspace.Destination);
        var result = await h.Engine.VerifyAsync(job, 1.0, Ct);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("checksum", StringComparison.OrdinalIgnoreCase) || e.Contains("decrypt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Errors, e => e.Contains(h.PasswordText, StringComparison.Ordinal));
        Assert.False(h.Log.Contains(h.PasswordText), "the repository password reached the log");
    }

    [Fact]
    public async Task A_job_without_backups_passes_with_nothing_checked()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_job_without_backups_passes_with_nothing_checked));
        Directory.CreateDirectory(h.Workspace.Source);

        var result = await h.Engine.VerifyAsync(h.Job(), 0.05, Ct);

        Assert.True(result.Passed);
        Assert.Equal(0, result.FilesRead);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public async Task The_sample_must_be_between_0_and_1(double fraction)
    {
        await using var h = await EngineHarness.CreateAsync(nameof(The_sample_must_be_between_0_and_1), createRepository: false);

        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.VerifyAsync(h.Job(), fraction, Ct));

        Assert.Equal(BackupErrorKind.InvalidRequest, ex.Kind);
    }

    [Fact]
    public async Task The_check_leaves_no_config_copy_or_cache_behind()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(The_check_leaves_no_config_copy_or_cache_behind));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.BackupAsync(job);
        var before = Directory.GetFileSystemEntries(h.Workspace.Data, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}logs", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{Path.DirectorySeparatorChar}cache", StringComparison.OrdinalIgnoreCase))
            .ToHashSet();

        await h.Engine.VerifyAsync(job, 0.5, Ct);

        var after = Directory.GetFileSystemEntries(h.Workspace.Data, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}logs", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{Path.DirectorySeparatorChar}cache", StringComparison.OrdinalIgnoreCase))
            .ToHashSet();
        Assert.Empty(after.Except(before));
    }

    private static void WriteRandomFiles(string root, int count, int size)
    {
        Directory.CreateDirectory(root);
        var random = new Random(20261015);
        for (var i = 0; i < count; i++)
        {
            var bytes = new byte[size];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, $"file-{i:00}.bin"), bytes);
        }
    }

    /// <summary>Flips bytes in the middle of every data pack ("p" blobs) of a filesystem repository.</summary>
    private static void CorruptDataPacks(string repository)
    {
        var packs = Directory.GetFiles(Path.Combine(repository, "p"), "*.f", SearchOption.AllDirectories);
        Assert.NotEmpty(packs);
        foreach (var pack in packs)
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
