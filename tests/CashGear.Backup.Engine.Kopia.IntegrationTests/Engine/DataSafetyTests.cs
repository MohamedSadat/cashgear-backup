using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>The rules where a mistake costs customer data (CLAUDE.md rules 2, 4, 5, 6, 8).</summary>
[Trait("Category", "Integration")]
public class DataSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Restore_into_a_non_empty_folder_or_onto_a_file_is_refused_and_changes_nothing()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Restore_into_a_non_empty_folder_or_onto_a_file_is_refused_and_changes_nothing));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "a.txt"), "from the backup");
        var job = h.Job();
        await h.BackupAsync(job);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];

        var target = h.Workspace.RestoreTarget("occupied");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "the user's newer file");
        var before = TreeManifest.Capture(target);

        var ex = await Assert.ThrowsAsync<BackupEngineException>(() =>
            h.Engine.RestoreAsync(new RestoreRequest { Snapshot = snapshot, TargetFolder = target }, Ct));
        Assert.Equal(BackupErrorKind.RestoreTargetNotEmpty, ex.Kind);
        Assert.Empty(before.ContentDifferences(TreeManifest.Capture(target)));
        Assert.Equal("the user's newer file", File.ReadAllText(Path.Combine(target, "a.txt")));

        var fileTarget = Path.Combine(target, "a.txt");
        ex = await Assert.ThrowsAsync<BackupEngineException>(() =>
            h.Engine.RestoreAsync(new RestoreRequest { Snapshot = snapshot, TargetFolder = fileTarget }, Ct));
        Assert.Equal(BackupErrorKind.RestoreTargetNotEmpty, ex.Kind);
        Assert.Empty(before.ContentDifferences(TreeManifest.Capture(target)));
    }

    [Fact]
    public async Task Repository_and_server_passwords_are_never_persisted_or_logged()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Repository_and_server_passwords_are_never_persisted_or_logged));
        TreeFixture.Create(h.Workspace.Source);
        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        await h.BackupAsync(job);
        await h.RestoreAsync((await h.Engine.ListSnapshotsAsync(job, Ct))[0], h.Workspace.RestoreTarget("r"));

        var host = h.Engine.HostFor(h.RepositoryId)!;
        var serverPasswords = host.CurrentCredentials!.Passwords.ToList();
        Assert.False(File.Exists(Path.Combine(host.Layout.Tls, "server.key")), "the TLS key file outlived the server start");

        // Restart the engine too, so the KOPIA_PASSWORD path is covered.
        await h.Engine.DisposeAsync();
        var engine = h.NewEngine();
        await engine.ConnectRepositoryAsync(h.Destination, h.Password, Ct);
        serverPasswords.AddRange(engine.HostFor(h.RepositoryId)!.CurrentCredentials!.Passwords);
        await engine.DisposeAsync();

        Assert.Empty(Directory.GetFiles(h.Workspace.Root, "*.kopia-password", SearchOption.AllDirectories));
        Assert.Empty(await CredentialManagerKopiaEntriesAsync());

        var secrets = serverPasswords.Prepend(h.PasswordText).ToList();
        var needles = secrets.SelectMany(s => new[]
        {
            Encoding.UTF8.GetBytes(s),
            Encoding.Unicode.GetBytes(s),
            Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        }).ToList();
        var leaks = Directory.EnumerateFiles(h.Workspace.Data, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(h.Workspace.Destination, "*", SearchOption.AllDirectories))
            .Where(f => needles.Any(n => File.ReadAllBytes(f).AsSpan().IndexOf(n) >= 0))
            .ToList();
        Assert.True(leaks.Count == 0, "secret found in: " + string.Join(", ", leaks));

        var log = h.Log.AllText;
        Assert.NotEmpty(log);
        Assert.All(secrets, s => Assert.DoesNotContain(s, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_file_makes_the_run_CompletedWithWarnings_never_Succeeded()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(An_unreadable_file_makes_the_run_CompletedWithWarnings_never_Succeeded));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "readable.txt"), "fine");
        var locked = Path.Combine(h.Workspace.Source, "unreadable.txt");
        File.WriteAllText(locked, "no access");
        var job = h.Job();

        // An explicit deny ACE: deterministic, and VSS (when elevated) does not bypass it.
        var info = new FileInfo(locked);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        RunResult result;
        try
        {
            result = await h.BackupAsync(job);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }

        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.NotNull(result.SnapshotId);
        Assert.Equal(1, result.FailedItemCount);
        var failed = Assert.Single(result.FailedItems);
        Assert.Equal("unreadable.txt", failed.Path);
        Assert.Contains("denied", failed.Error, StringComparison.OrdinalIgnoreCase);

        var snapshot = Assert.Single(await h.Engine.ListSnapshotsAsync(job, Ct));
        Assert.Equal(1, snapshot.FailedItemCount);
        Assert.True(snapshot.IsComplete);
    }

    [Fact]
    public async Task A_canceled_backup_is_Canceled_and_not_a_restore_point()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_canceled_backup_is_Canceled_and_not_a_restore_point));
        var random = new Random(7);
        for (var i = 0; i < 3; i++)
        {
            var bytes = new byte[8 * 1024 * 1024];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(h.Workspace.Source, $"random-{i}.bin"), bytes);
        }

        var job = h.Job();
        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        var client = await h.ClientAsync();
        await client.SetThrottleAsync(new ThrottleLimits { MaxUploadSpeedBytesPerSecond = 4_000_000 }, Ct);

        var run = await h.Engine.StartBackupAsync(job, Ct);
        await h.Engine.CancelAsync(run, Ct);
        var result = (await h.RunToEndAsync(run)).Result!;

        Assert.Equal(RunState.Canceled, result.State);
        Assert.Null(result.SnapshotId);
        Assert.All(await h.Engine.ListSnapshotsAsync(job, Ct), s => Assert.False(s.IsComplete));

        // Cancel after the end is a no-op, and the next run succeeds.
        await h.Engine.CancelAsync(run, Ct);
        await client.SetThrottleAsync(new ThrottleLimits(), Ct);
        var next = await h.BackupAsync(job);
        Assert.Equal(RunState.Succeeded, next.State);
        Assert.Contains(await h.Engine.ListSnapshotsAsync(job, Ct), s => s.Id == next.SnapshotId && s.IsComplete);
    }

    [Fact]
    public async Task Retention_keeps_exactly_what_the_job_says_and_always_the_newest()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Retention_keeps_exactly_what_the_job_says_and_always_the_newest));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "unchanged.txt"), "same content every run");
        var job = h.Job() with
        {
            Retention = new RetentionRule { KeepLatest = 2, KeepHourly = 0, KeepDaily = 0, KeepWeekly = 0, KeepMonthly = 0, KeepAnnual = 0 },
        };
        await h.Engine.ApplyJobPolicyAsync(job, Ct);

        // Unchanged source: each run must still record its own snapshot (ignoreIdenticalSnapshots is off).
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var result = await h.BackupAsync(job);
            Assert.Equal(RunState.Succeeded, result.State);
            ids.Add(result.SnapshotId!);
        }

        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal([ids[2], ids[1]], (await h.Engine.ListSnapshotsAsync(job, Ct)).Select(s => s.Id));
    }

    [Fact]
    public async Task Retention_that_could_prune_the_newest_snapshot_is_rejected_before_anything_is_written()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Retention_that_could_prune_the_newest_snapshot_is_rejected_before_anything_is_written));
        var job = h.Job() with { Retention = new RetentionRule { KeepLatest = 0 } };

        Assert.Equal(BackupErrorKind.InvalidRequest, (await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.ApplyJobPolicyAsync(job, Ct))).Kind);
        Assert.Equal(BackupErrorKind.InvalidRequest, (await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.StartBackupAsync(job, Ct))).Kind);
        Assert.Null(await (await h.ClientAsync()).GetPolicyAsync(h.Engine.SourceFor(job), Ct));
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_InvalidPassword_with_and_without_local_config()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_wrong_password_is_reported_as_InvalidPassword_with_and_without_local_config));
        await h.Engine.DisposeAsync();
        using var wrong = new SecureSecret("not-the-password");

        var withConfig = h.NewEngine();
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => withConfig.ConnectRepositoryAsync(h.Destination, wrong, Ct));
        Assert.Equal(BackupErrorKind.InvalidPassword, ex.Kind);
        Assert.Null(withConfig.HostFor(h.RepositoryId));

        var withoutConfig = h.NewEngine(dataDirectory: Path.Combine(h.Workspace.Root, "data-2"));
        ex = await Assert.ThrowsAsync<BackupEngineException>(() => withoutConfig.ConnectRepositoryAsync(h.Destination, wrong, Ct));
        Assert.Equal(BackupErrorKind.InvalidPassword, ex.Kind);

        // The failed attempts left nothing behind that blocks the right password.
        Assert.Equal(h.RepositoryId, (await withConfig.ConnectRepositoryAsync(h.Destination, h.Password, Ct)).RepositoryId);
        Assert.Equal(h.RepositoryId, (await withoutConfig.ConnectRepositoryAsync(h.Destination, h.Password, Ct)).RepositoryId);
    }

    [Fact]
    public async Task Creating_over_an_existing_repository_is_refused()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Creating_over_an_existing_repository_is_refused));
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.CreateRepositoryAsync(h.Destination, h.Password, Ct));
        Assert.Equal(BackupErrorKind.RepositoryAlreadyExists, ex.Kind);

        var other = h.NewEngine(dataDirectory: Path.Combine(h.Workspace.Root, "data-2"));
        ex = await Assert.ThrowsAsync<BackupEngineException>(() => other.CreateRepositoryAsync(h.Destination, h.Password, Ct));
        Assert.Equal(BackupErrorKind.RepositoryAlreadyExists, ex.Kind);
    }

    [Fact]
    public async Task A_tampered_or_unpinned_kopia_exe_is_never_started()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_tampered_or_unpinned_kopia_exe_is_never_started), createRepository: false);
        var tampered = Path.Combine(h.Workspace.Root, "kopia.exe");
        File.Copy(KopiaTestBinary.ExePath, tampered);
        using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            var b = stream.ReadByte();
            stream.Position = stream.Length / 2;
            stream.WriteByte((byte)(b ^ 0x01));
        }

        var engine = h.NewEngine(configure: o => o with { KopiaExePath = tampered });
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => engine.CreateRepositoryAsync(h.Destination, h.Password, Ct));
        Assert.Equal(BackupErrorKind.EngineBinaryRejected, ex.Kind);
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(h.Workspace.Destination));
        Assert.DoesNotContain(Process.GetProcessesByName("kopia"), p => IsImage(p, tampered));

        var invalid = Assert.Throws<BackupEngineException>(() => new KopiaBackupEngine(h.Options() with { KopiaExePath = "kopia.exe" }));
        Assert.Equal(BackupErrorKind.InvalidRequest, invalid.Kind);
    }

    [Fact]
    public async Task A_second_backup_of_a_running_job_is_refused()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_second_backup_of_a_running_job_is_refused));
        File.WriteAllText(Path.Combine(h.Workspace.Source, "a.txt"), "a");
        var job = h.Job();
        var run = await h.Engine.StartBackupAsync(job, Ct);

        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.StartBackupAsync(job with { Id = Guid.NewGuid() }, Ct));
        Assert.Equal(BackupErrorKind.RunAlreadyActive, ex.Kind);

        Assert.Equal(RunState.Succeeded, (await h.RunToEndAsync(run)).State);
        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
    }

    [Fact]
    public async Task A_missing_source_folder_is_reported_before_anything_runs()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(A_missing_source_folder_is_reported_before_anything_runs));
        var job = h.Job(Path.Combine(h.Workspace.Root, "unplugged-drive"));
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.StartBackupAsync(job, Ct));
        Assert.Equal(BackupErrorKind.SourceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task Operations_on_a_repository_that_is_not_connected_fail_clearly()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(Operations_on_a_repository_that_is_not_connected_fail_clearly), createRepository: false);
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.ListSnapshotsAsync(h.Job(), Ct));
        Assert.Equal(BackupErrorKind.RepositoryNotConnected, ex.Kind);

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Engine.VerifyAsync(h.Job(), 0.05, Ct));
    }

    private static bool IsImage(Process process, string path)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<List<string>> CredentialManagerKopiaEntriesAsync()
    {
        var psi = new ProcessStartInfo("cmdkey", "/list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return [.. output.Split('\n').Where(l => l.Contains("kopia", StringComparison.OrdinalIgnoreCase) || l.Contains("repository.config", StringComparison.OrdinalIgnoreCase))];
    }
}
