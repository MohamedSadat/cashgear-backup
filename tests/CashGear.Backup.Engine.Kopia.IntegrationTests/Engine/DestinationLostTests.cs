using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>
/// The destination goes away in the middle of a backup (docs/hardening.md, finding 3): Kopia never fails the snapshot
/// for that, so the engine has to end the run itself, as Failed and saying why, and keep a new run of the folder from
/// starting on top of Kopia's upload while that one still stops. The destination sits behind a <see cref="ChaosProxy"/>;
/// cutting it resets every connection, as an unplugged network does. Each case is its own class so they run in parallel.
/// </summary>
public static class DestinationLostTests
{
    /// <summary>Shorter than the product's 90 s / 30 s so the tests stay quick; the rule is the same.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => EngineHarness.Ct;

    [Trait("Category", "Integration")]
    public sealed class NetworkToSftp
    {
        [Fact]
        public Task Goes_down_mid_backup_the_run_ends_Failed_by_itself_and_the_next_one_succeeds() => NetworkDownAsync("sftp");
    }

    [Trait("Category", "Integration")]
    public sealed class NetworkToS3
    {
        [Fact]
        public Task Goes_down_mid_backup_the_run_ends_Failed_by_itself_and_the_next_one_succeeds() => NetworkDownAsync("s3");
    }

    [Trait("Category", "Integration")]
    public sealed class FullSftpServer
    {
        [Fact]
        public Task Ends_the_backup_Failed_saying_it_is_full() => FullSftpAsync();
    }

    [Trait("Category", "Integration")]
    public sealed class LocalFolder
    {
        [Fact]
        public Task That_refuses_writes_ends_the_backup_Failed_saying_access_is_denied() => LocalFolderRefusesWritesAsync();
    }

    private static async Task NetworkDownAsync(string kind)
    {
        await using var remote = await Remote.StartAsync(kind, Ct);
        await using var h = await EngineHarness.CreateAsync($"destination-lost-{kind}", configure: Quick, destination: remote.Destination);
        var job = h.Job();
        var run = await StartThrottledBackupAsync(h, job, remote.Proxy, cutAfterBytes: 16 * MiB);

        remote.Proxy.Cut();
        var sinceCut = Stopwatch.StartNew();
        var lost = (await h.RunToEndAsync(run)).Result!;
        var took = sinceCut.Elapsed;

        // Rule 5: nothing recorded. It ends on its own once Kopia has given up a write and kept failing for the window
        // (S3 gives up its first write after about 50 s, SFTP after about 10 s).
        Assert.Equal(RunState.Failed, lost.State);
        Assert.Null(lost.SnapshotId);
        Assert.StartsWith("Lost the destination: it could not be reached for", lost.Message, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromSeconds(100), $"the run took {took} to end after the network went down");
        Assert.True(h.Log.Contains("lost its destination"), "the engine did not log why it ended the run");

        // Kopia is still retrying the upload: a new run of the folder waits for it, then is refused, saying why.
        var waited = Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.StartBackupAsync(job, Ct));
        Assert.Equal(BackupErrorKind.RunAlreadyActive, refused.Kind);
        Assert.Contains("still stopping", refused.Message, StringComparison.Ordinal);
        Assert.True(waited.Elapsed >= TimeSpan.FromSeconds(4), $"the new run was refused after {waited.Elapsed} without waiting");

        remote.Proxy.Restore();
        await (await h.ClientAsync()).SetThrottleAsync(new ThrottleLimits(), Ct);
        var next = await BackupOnceKopiaHasStoppedAsync(h, job);
        Assert.True(next.State == RunState.Succeeded, $"the run after the outage was {next.State}: {next.Message}");

        Assert.False(h.Log.Contains(remote.Secret), "the destination's credential reached the log");
        await AssertRestoresIdenticallyAsync(h, job, next, kind);
    }

    private static async Task FullSftpAsync()
    {
        var root = TempRoot();
        await using var nas = await QuotaSftpServer.StartAsync(root, quotaBytes: 64 * MiB, Ct);
        await using var h = await EngineHarness.CreateAsync("destination-full-sftp", configure: Quick,
            destination: hh => Task.FromResult<DestinationSpec>(Sftp(hh, nas.Proxy, nas.Username, nas.Password, nas.KnownHosts)));
        var job = h.Job();
        var run = await StartThrottledBackupAsync(h, job, nas.Proxy, cutAfterBytes: 0);

        var full = (await h.RunToEndAsync(run)).Result!;

        Assert.Equal(RunState.Failed, full.State);
        Assert.Null(full.SnapshotId);
        Assert.StartsWith("Lost the destination: it is full", full.Message, StringComparison.Ordinal);

        // Space freed: the next run succeeds, reusing what reached the destination, and restores byte for byte.
        await nas.RestartAsync(quotaBytes: 0, Ct);
        await (await h.ClientAsync()).SetThrottleAsync(new ThrottleLimits(), Ct);
        var next = await BackupOnceKopiaHasStoppedAsync(h, job);
        Assert.True(next.State == RunState.Succeeded, $"the run after space was freed was {next.State}: {next.Message}");
        await AssertRestoresIdenticallyAsync(h, job, next, "full");
    }

    private static async Task LocalFolderRefusesWritesAsync()
    {
        await using var h = await EngineHarness.CreateAsync("destination-refuses-writes", configure: Quick);
        var job = h.Job();
        var run = await StartThrottledBackupAsync(h, job, proxy: null, cutAfterBytes: 0);
        var packs = Path.Combine(h.Workspace.Destination, "p");
        await WaitForAsync(() => Directory.Exists(packs) && Directory.EnumerateFiles(packs, "*", SearchOption.AllDirectories).Any(), "the first pack to be written");

        // The disk, or the share behind the folder, stops taking writes (a read-only or full volume answers the same way).
        var deny = DenyWrites(h.Workspace.Destination);
        RunResult refused;
        try
        {
            refused = (await h.RunToEndAsync(run)).Result!;
        }
        finally
        {
            AllowWrites(h.Workspace.Destination, deny);
        }

        Assert.Equal(RunState.Failed, refused.State);
        Assert.Null(refused.SnapshotId);
        Assert.StartsWith("Lost the destination: it refused every write", refused.Message, StringComparison.Ordinal);

        await (await h.ClientAsync()).SetThrottleAsync(new ThrottleLimits(), Ct);
        var next = await BackupOnceKopiaHasStoppedAsync(h, job);
        Assert.True(next.State == RunState.Succeeded, $"the run after writes were allowed again was {next.State}: {next.Message}");
        await AssertRestoresIdenticallyAsync(h, job, next, "local");
    }

    private const long MiB = 1024 * 1024;

    private static KopiaEngineOptions Quick(KopiaEngineOptions o) => o with
    {
        DestinationLossWindow = Window,
        DestinationQuietPeriod = TimeSpan.FromSeconds(10),
        TaskLogInterval = TimeSpan.FromSeconds(1),
        StoppingRunWait = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// 128 MiB of random data in 32 files, uploaded at 4 MB/s so the run lasts long enough; returns once
    /// <paramref name="cutAfterBytes"/> have gone through the proxy.
    /// </summary>
    private static async Task<RunHandle> StartThrottledBackupAsync(EngineHarness h, BackupJob job, ChaosProxy? proxy, long cutAfterBytes)
    {
        var random = new Random(11);
        var bytes = new byte[4 * MiB];
        for (var i = 0; i < 32; i++)
        {
            random.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(h.Workspace.Source, $"random-{i:D2}.bin"), bytes, Ct);
        }

        await h.Engine.ApplyJobPolicyAsync(job, Ct);
        await (await h.ClientAsync()).SetThrottleAsync(new ThrottleLimits { MaxUploadSpeedBytesPerSecond = 4_000_000 }, Ct);
        var sentBefore = proxy?.BytesToServer ?? 0;
        var run = await h.Engine.StartBackupAsync(job, Ct);
        await WaitForAsync(() => (proxy?.BytesToServer ?? 0) - sentBefore >= cutAfterBytes, "the backup to send enough to cut it part way");
        return run;
    }

    /// <summary>The only complete snapshot is the one <paramref name="next"/> recorded, and it restores byte for byte.</summary>
    private static async Task AssertRestoresIdenticallyAsync(EngineHarness h, BackupJob job, RunResult next, string name)
    {
        var snapshot = Assert.Single(await h.Engine.ListSnapshotsAsync(job, Ct), s => s.IsComplete);
        Assert.Equal(next.SnapshotId, snapshot.Id);
        var target = h.Workspace.RestoreTarget(name);
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, target)).State);
        EngineHarness.AssertIdentical(TreeManifest.Capture(h.Workspace.Source), target);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < EngineHarness.RunTimeout, $"timed out waiting for {what}");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Denies this user creating and writing files anywhere under <paramref name="folder"/>, as a read-only volume would.</summary>
    private static FileSystemAccessRule DenyWrites(string folder)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.AppendData | FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        var directory = new DirectoryInfo(folder);
        var acl = directory.GetAccessControl();
        acl.AddAccessRule(rule);
        directory.SetAccessControl(acl);
        return rule;
    }

    private static void AllowWrites(string folder, FileSystemAccessRule deny)
    {
        var directory = new DirectoryInfo(folder);
        var acl = directory.GetAccessControl();
        acl.RemoveAccessRuleSpecific(deny);
        directory.SetAccessControl(acl);
    }

    /// <summary>The next run, once Kopia has stopped the one the engine ended (the test does not know when exactly).</summary>
    private static async Task<RunResult> BackupOnceKopiaHasStoppedAsync(EngineHarness h, BackupJob job)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await h.BackupAsync(job);
            }
            catch (BackupEngineException ex) when (ex.Kind == BackupErrorKind.RunAlreadyActive && stopwatch.Elapsed < TimeSpan.FromMinutes(1))
            {
            }
        }
    }

    private static SftpDestination Sftp(EngineHarness h, ChaosProxy proxy, string user, string password, IEnumerable<string> knownHosts) =>
        new(h.RepositoryId, proxy.Host, proxy.Port, user, "/repo", [.. knownHosts.Select(line => $"[{proxy.Host}]:{proxy.Port} {line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]}")])
        {
            Password = new SecureSecret(password),
        };

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "servers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>An SFTP or S3 test server that the engine reaches only through a proxy.</summary>
    private sealed class Remote(IAsyncDisposable server, ChaosProxy proxy, string secret, Func<EngineHarness, DestinationSpec> destination) : IAsyncDisposable
    {
        public ChaosProxy Proxy { get; } = proxy;

        /// <summary>The SFTP password or S3 secret key, for leak checks.</summary>
        public string Secret { get; } = secret;

        public Func<EngineHarness, Task<DestinationSpec>> Destination => h => Task.FromResult(destination(h));

        public static async Task<Remote> StartAsync(string kind, CancellationToken ct)
        {
            if (kind == "sftp")
            {
                var sftp = await SftpTestServer.StartAsync(TempRoot(), ct);
                var proxy = new ChaosProxy(sftp.Port);
                return new Remote(sftp, proxy, sftp.Password, h => Sftp(h, proxy, sftp.Username, sftp.Password, sftp.KnownHosts));
            }

            var s3 = await S3TestServer.StartAsync(TempRoot(), ct);
            var s3Proxy = new ChaosProxy(s3.Port);
            var bucket = s3.CreateBucket("backups");
            return new Remote(s3, s3Proxy, s3.SecretAccessKey, h =>
                new S3Destination(h.RepositoryId, $"{s3Proxy.Host}:{s3Proxy.Port}", bucket, "pc1/", s3.AccessKeyId, new SecureSecret(s3.SecretAccessKey)) { UseTls = false });
        }

        public async ValueTask DisposeAsync()
        {
            await Proxy.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
