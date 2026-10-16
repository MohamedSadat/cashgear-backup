using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

/// <summary>
/// SFTP and S3 destinations against the pinned test servers (SFTPGo, versitygw) on 127.0.0.1. Kopia needs every
/// host key an SFTP server offers in its known-hosts data, and it would write S3 keys into its config file, so the
/// engine hands S3 keys to the server process through its environment instead (FINDINGS, Phase 4).
/// </summary>
[Trait("Category", "Integration")]
public class RemoteDestinationTests
{
    private static CancellationToken Ct => EngineHarness.Ct;

    [Fact]
    public async Task Sftp_round_trip_and_a_second_pc_connects()
    {
        await using var sftp = await SftpTestServer.StartAsync(TempRoot(), Ct);
        await using var h = await EngineHarness.CreateAsync(nameof(Sftp_round_trip_and_a_second_pc_connects), destination: hh => Task.FromResult<DestinationSpec>(Sftp(hh, sftp)));
        TreeFixture.Create(h.Workspace.Source);
        var manifest = TreeManifest.Capture(h.Workspace.Source);
        var job = h.Job();

        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];
        var target = h.Workspace.RestoreTarget("sftp");
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, target)).State);
        EngineHarness.AssertIdentical(manifest, target);

        var other = h.NewEngine(Path.Combine(h.Workspace.Root, "second-pc"));
        await other.ConnectRepositoryAsync(Sftp(h, sftp), h.Password, Ct);
        Assert.Single(await other.ListSnapshotsAsync(job, Ct));
        Assert.False(h.Log.Contains(sftp.Password), "the SFTP password reached the log");
        Assert.False(h.Log.Contains(h.PasswordText), "the repository password reached the log");
    }

    [Fact]
    public async Task An_sftp_server_whose_host_key_changed_is_refused()
    {
        await using var sftp = await SftpTestServer.StartAsync(TempRoot(), Ct);
        await using var impostor = await SftpTestServer.StartAsync(TempRoot(), Ct);
        await using var h = await EngineHarness.CreateAsync(nameof(An_sftp_server_whose_host_key_changed_is_refused), createRepository: false);

        // The keys of one server, presented for another one's address: a man in the middle.
        var known = sftp.KnownHosts.Select(line => line.Replace($":{sftp.Port}]", $":{impostor.Port}]", StringComparison.Ordinal)).ToList();
        var dest = new SftpDestination(h.RepositoryId, impostor.Host, impostor.Port, impostor.Username, "/repo", known) { Password = new SecureSecret(impostor.Password) };
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.CreateRepositoryAsync(dest, h.Password, Ct));

        Assert.Equal(BackupErrorKind.DestinationNotTrusted, ex.Kind);
        Assert.Empty(Directory.GetFileSystemEntries(impostor.Root));
    }

    [Fact]
    public async Task A_wrong_sftp_password_is_access_denied()
    {
        await using var sftp = await SftpTestServer.StartAsync(TempRoot(), Ct);
        await using var h = await EngineHarness.CreateAsync(nameof(A_wrong_sftp_password_is_access_denied), createRepository: false);

        var dest = new SftpDestination(h.RepositoryId, sftp.Host, sftp.Port, sftp.Username, "/repo", sftp.KnownHosts) { Password = new SecureSecret("not-the-password") };
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.CreateRepositoryAsync(dest, h.Password, Ct));

        Assert.Equal(BackupErrorKind.DestinationAccessDenied, ex.Kind);
        Assert.DoesNotContain("not-the-password", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_round_trip_keeps_the_secret_key_out_of_every_file_and_a_second_pc_connects()
    {
        await using var s3 = await S3TestServer.StartAsync(TempRoot(), Ct);
        await using var h = await EngineHarness.CreateAsync(nameof(S3_round_trip_keeps_the_secret_key_out_of_every_file_and_a_second_pc_connects),
            destination: hh => Task.FromResult<DestinationSpec>(S3(hh, s3, s3.CreateBucket("backups"))));
        TreeFixture.Create(h.Workspace.Source);
        var manifest = TreeManifest.Capture(h.Workspace.Source);
        var job = h.Job();

        Assert.Equal(RunState.Succeeded, (await h.BackupAsync(job)).State);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];
        var target = h.Workspace.RestoreTarget("s3");
        Assert.Equal(RunState.Succeeded, (await h.RestoreAsync(snapshot, target)).State);
        EngineHarness.AssertIdentical(manifest, target);

        foreach (var file in Directory.GetFiles(h.Workspace.Data, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length < 4 * 1024 * 1024))
        {
            Assert.DoesNotContain(s3.SecretAccessKey, ReadShared(file), StringComparison.Ordinal);
        }

        Assert.False(h.Log.Contains(s3.SecretAccessKey), "the S3 secret key reached the log");
        var other = h.NewEngine(Path.Combine(h.Workspace.Root, "second-pc"));
        await other.ConnectRepositoryAsync(S3(h, s3, "backups"), h.Password, Ct);
        Assert.Single(await other.ListSnapshotsAsync(job, Ct));
    }

    [Fact]
    public async Task A_wrong_s3_secret_is_access_denied()
    {
        await using var s3 = await S3TestServer.StartAsync(TempRoot(), Ct);
        await using var h = await EngineHarness.CreateAsync(nameof(A_wrong_s3_secret_is_access_denied), createRepository: false);

        var dest = new S3Destination(h.RepositoryId, s3.Endpoint, s3.CreateBucket("backups"), "", s3.AccessKeyId, new SecureSecret("wrong-secret-key-123456")) { UseTls = false };
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => h.Engine.CreateRepositoryAsync(dest, h.Password, Ct));

        Assert.Equal(BackupErrorKind.DestinationAccessDenied, ex.Kind);
    }

    [Fact]
    public async Task Remote_destinations_validate_their_settings()
    {
        var id = Guid.NewGuid();
        Assert.NotEmpty(new SftpDestination(id, "", 22, "u", "/p", ["[h]:22 ssh-ed25519 AAAA"]) { Password = new SecureSecret("x") }.Validate());
        Assert.NotEmpty(new SftpDestination(id, "h", 22, "u", "/p", []) { Password = new SecureSecret("x") }.Validate());
        Assert.NotEmpty(new SftpDestination(id, "h", 22, "u", "/p", ["[h]:22 ssh-ed25519 AAAA"]).Validate());
        Assert.NotEmpty(new S3Destination(id, "s3.example.com", "", "", "key", new SecureSecret("secret")).Validate());
        Assert.Empty(new S3Destination(id, "s3.example.com", "bucket", "pc1/", "key", new SecureSecret("secret")).Validate());
        Assert.Equal("sftp://u@h:2222/p", new SftpDestination(id, "h", 2222, "u", "/p", ["x"]) { Password = new SecureSecret("x") }.DisplayLocation);
        Assert.Equal("s3://bucket/pc1/ at s3.example.com", new S3Destination(id, "s3.example.com", "bucket", "pc1/", "key", new SecureSecret("secret")).DisplayLocation);
        await Task.CompletedTask;
    }

    private static SftpDestination Sftp(EngineHarness h, SftpTestServer sftp) =>
        new(h.RepositoryId, sftp.Host, sftp.Port, sftp.Username, "/repo", sftp.KnownHosts) { Password = new SecureSecret(sftp.Password) };

    private static S3Destination S3(EngineHarness h, S3TestServer s3, string bucket) =>
        new(h.RepositoryId, s3.Endpoint, bucket, "pc1/", s3.AccessKeyId, new SecureSecret(s3.SecretAccessKey)) { UseTls = false };

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "servers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
