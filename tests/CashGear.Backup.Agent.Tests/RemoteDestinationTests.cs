using CashGear.Backup.Agent.Destinations;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Core;
using CashGear.Backup.Testing;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using DestinationKind = CashGear.Backup.Contracts.V1.DestinationKind;
using RunState = CashGear.Backup.Contracts.V1.RunState;

namespace CashGear.Backup.Agent.Tests;

/// <summary>SFTP, S3 and network-share destinations through the agent's API, against the pinned local test servers.</summary>
[Trait("Category", "Integration")]
public class RemoteDestinationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_sftp_destination_backs_up_and_reconnects_after_a_restart()
    {
        await using var h = await AgentHarness.StartAsync(nameof(An_sftp_destination_backs_up_and_reconnects_after_a_restart));
        await using var sftp = await SftpTestServer.StartAsync(h.Root, Ct);
        RestoreToOriginalTests.WriteSource(h.Source);

        var destination = await h.Client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "NAS over SFTP", Kind = DestinationKind.Sftp, Password = h.Password, Sftp = Sftp(sftp),
        }, cancellationToken: Ct);

        Assert.Equal(DestinationKind.Sftp, destination.Kind);
        Assert.Equal($"sftp://{sftp.Username}@{sftp.Host}:{sftp.Port}/backups/pc1", destination.Location);
        var job = await h.CreateJobAsync(destination.Id);
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);

        await h.RestartAsync();
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);
        Assert.Equal(DestinationState.Connected, (await h.Client.ListDestinationsAsync(new ListDestinationsRequest(), cancellationToken: Ct)).Destinations.Single().State);
        await AssertNotStoredInClearAsync(h, sftp.Password);
    }

    [Fact]
    public async Task An_s3_destination_backs_up_and_reconnects_after_a_restart_without_its_secret_in_clear()
    {
        await using var h = await AgentHarness.StartAsync(nameof(An_s3_destination_backs_up_and_reconnects_after_a_restart_without_its_secret_in_clear));
        await using var s3 = await S3TestServer.StartAsync(h.Root, Ct);
        RestoreToOriginalTests.WriteSource(h.Source);

        var destination = await h.Client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "Bucket", Kind = DestinationKind.S3, Password = h.Password, S3 = S3(s3, s3.CreateBucket("backups")),
        }, cancellationToken: Ct);

        Assert.Equal("s3://backups/pc1/ at " + s3.Endpoint, destination.Location);
        var job = await h.CreateJobAsync(destination.Id);
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);

        await h.RestartAsync();
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);
        await AssertNotStoredInClearAsync(h, s3.SecretAccessKey);
        foreach (var file in Directory.GetFiles(h.Data, "*", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length < 4 * 1024 * 1024))
        {
            Assert.DoesNotContain(s3.SecretAccessKey, ReadShared(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_offline_sftp_server_is_a_clear_failed_run()
    {
        await using var h = await AgentHarness.StartAsync(nameof(An_offline_sftp_server_is_a_clear_failed_run));
        RestoreToOriginalTests.WriteSource(h.Source);
        Contracts.V1.Job job;
        await using (var sftp = await SftpTestServer.StartAsync(h.Root, Ct))
        {
            var destination = await h.Client.CreateDestinationAsync(new CreateDestinationRequest
            {
                Name = "NAS", Kind = DestinationKind.Sftp, Password = h.Password, Sftp = Sftp(sftp),
            }, cancellationToken: Ct);
            job = await h.CreateJobAsync(destination.Id);
        }

        var run = await h.RunNowAsync(job.Id);

        Assert.Equal(RunState.Failed, run.State);
        Assert.Contains("not reachable", run.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_remote_settings_are_refused_before_anything_connects()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Incomplete_remote_settings_are_refused_before_anything_connects));
        var noHostKeys = new SftpSettings { Host = "nas.example.test", Username = "u", Password = "pw-1234567", Path = "/b" };
        var noSecret = new S3Settings { Endpoint = "s3.example.test", Bucket = "b", AccessKeyId = "AKIA" };
        var notAShare = new ShareSettings { Username = "u", Password = "p" };

        foreach (var request in new[]
        {
            new CreateDestinationRequest { Name = "x", Kind = DestinationKind.Sftp, Password = h.Password, Sftp = noHostKeys },
            new CreateDestinationRequest { Name = "x", Kind = DestinationKind.S3, Password = h.Password, S3 = noSecret },
            new CreateDestinationRequest { Name = "x", Kind = DestinationKind.NetworkShare, Location = @"C:\not\a\share", Password = h.Password, Share = notAShare },
            new CreateDestinationRequest { Name = "x", Kind = DestinationKind.NetworkShare, Location = @"\\nas\share\pc", Password = h.Password, Share = new ShareSettings() },
        })
        {
            var ex = await Assert.ThrowsAsync<RpcException>(() => h.Client.CreateDestinationAsync(request, cancellationToken: Ct).ResponseAsync);
            Assert.Equal("InvalidArgument", AgentHarness.ErrorKind(ex));
        }

        Assert.Empty((await h.Client.ListDestinationsAsync(new ListDestinationsRequest(), cancellationToken: Ct)).Destinations);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc1", @"\\nas\backups")]
    [InlineData(@"\\nas\backups", @"\\nas\backups")]
    [InlineData(@"\\nas", null)]
    [InlineData(@"C:\backups", null)]
    [InlineData(@"\\?\C:\backups", null)]
    public void The_share_root_of_a_unc_path(string path, string? root) => Assert.Equal(root, ShareConnection.ShareRoot(path));

    [Theory]
    [InlineData(0, null)]
    [InlineData(85, null)]
    [InlineData(1326, BackupErrorKind.DestinationAccessDenied)]
    [InlineData(1219, BackupErrorKind.DestinationAccessDenied)]
    [InlineData(53, BackupErrorKind.DestinationUnreachable)]
    public void Share_errors_say_what_went_wrong(int error, BackupErrorKind? kind) => Assert.Equal(kind, ShareConnection.Classify(error)?.Kind);

    /// <summary>
    /// Runs only against a real share: set CASHGEAR_TEST_SMB_SHARE (\\server\share\folder), CASHGEAR_TEST_SMB_USER and
    /// CASHGEAR_TEST_SMB_PASSWORD. Creating a share needs administrator rights, so the default test run has none.
    /// </summary>
    [Fact]
    public async Task A_network_share_with_its_own_account_backs_up()
    {
        var share = Environment.GetEnvironmentVariable("CASHGEAR_TEST_SMB_SHARE");
        Assert.SkipWhen(string.IsNullOrEmpty(share), "Set CASHGEAR_TEST_SMB_SHARE, CASHGEAR_TEST_SMB_USER and CASHGEAR_TEST_SMB_PASSWORD to run against a real share.");
        await using var h = await AgentHarness.StartAsync(nameof(A_network_share_with_its_own_account_backs_up));
        RestoreToOriginalTests.WriteSource(h.Source);

        var destination = await h.Client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "NAS", Kind = DestinationKind.NetworkShare, Location = Path.Combine(share!, "cashgear-test-" + Guid.NewGuid().ToString("N")[..8]),
            Password = h.Password,
            Share = new ShareSettings { Username = Environment.GetEnvironmentVariable("CASHGEAR_TEST_SMB_USER"), Password = Environment.GetEnvironmentVariable("CASHGEAR_TEST_SMB_PASSWORD") },
        }, cancellationToken: Ct);

        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync((await h.CreateJobAsync(destination.Id)).Id)).State);
    }

    internal static SftpSettings Sftp(SftpTestServer sftp)
    {
        var settings = new SftpSettings { Host = sftp.Host, Port = sftp.Port, Username = sftp.Username, Password = sftp.Password, Path = "/backups/pc1" };
        settings.KnownHosts.AddRange(sftp.KnownHosts);
        return settings;
    }

    internal static S3Settings S3(S3TestServer s3, string bucket) => new()
    {
        Endpoint = s3.Endpoint, Bucket = bucket, Prefix = "pc1/", AccessKeyId = s3.AccessKeyId, SecretAccessKey = s3.SecretAccessKey, InsecureHttp = true,
    };

    private static async Task AssertNotStoredInClearAsync(AgentHarness h, string secret)
    {
        await h.StopAgentAsync();
        await h.WithStateDbAsync(async db =>
        {
            var destination = await db.Destinations.SingleAsync(Ct);
            Assert.DoesNotContain(secret, destination.Settings ?? "", StringComparison.Ordinal);
            Assert.NotNull(destination.ProtectedCredential);
        });
        await h.StartAgentAsync();
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
