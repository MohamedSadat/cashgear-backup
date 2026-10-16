using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Agent.Updates;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Testing;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.Agent.Tests;

/// <summary>
/// Signed updates against a fake feed. The "MSI" is the pinned engine binary: a real Authenticode-signed file, so
/// the Windows signature check runs for real; only msiexec is replaced.
/// </summary>
[Trait("Category", "Integration")]
public class UpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] SignedPackage => File.ReadAllBytes(KopiaTestBinary.ExePath);

    private static string SignerThumbprint
    {
        get
        {
#pragma warning disable SYSLIB0057 // Reads the signer of a signed file; there is no loader replacement for this.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(KopiaTestBinary.ExePath));
#pragma warning restore SYSLIB0057
            return signer.GetCertHashString(HashAlgorithmName.SHA256);
        }
    }

    [Fact]
    public async Task Without_a_feed_key_and_publisher_nothing_is_checked()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", SignedPackage);
        await using var h = await AgentHarness.StartAsync(nameof(Without_a_feed_key_and_publisher_nothing_is_checked), o => o.UpdateManifestUrl = feed.ManifestUrl);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.NotConfigured, status.State);
        Assert.Equal(0, feed.Downloads);
    }

    [Fact]
    public async Task A_newer_signed_update_is_verified_and_installed_and_new_runs_wait()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", SignedPackage);
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(A_newer_signed_update_is_verified_and_installed_and_new_runs_wait), feed, installer);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal((UpdateState.Installing, "99.0.0"), (status.State, status.AvailableVersion));
        var (msi, log) = Assert.Single(installer.Started);
        Assert.Equal(Path.Combine(h.Data, "updates", "CashGearBackup-99.0.0.msi"), msi);
        Assert.Equal(Path.Combine(h.Data, "updates", "install-99.0.0.log"), log);
        Assert.Equal(SignedPackage, await File.ReadAllBytesAsync(msi, Ct));

        var blocked = await Assert.ThrowsAsync<RpcException>(() => h.Client.RunJobNowAsync(new RunJobNowRequest { JobId = job.Id }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal(RunCoordinator.MaintenanceMessage, blocked.Status.Detail);
    }

    [Fact]
    public async Task An_update_waits_until_no_backup_runs()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", SignedPackage);
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(An_update_waits_until_no_backup_runs), feed, installer);
        RestoreToOriginalTests.WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var runs = h.App.Services.GetRequiredService<RunCoordinator>();
        var updates = h.App.Services.GetRequiredService<UpdateService>();

        // Downloaded while the gate is held (as if another install were on), so it is only ready.
        Assert.True(runs.TryBeginMaintenance());
        Assert.Equal(UpdateState.Ready, (await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct)).State);
        runs.EndMaintenance();

        // A backup runs: the next round leaves the update ready.
        var run = await h.Client.RunJobNowAsync(new RunJobNowRequest { JobId = job.Id }, cancellationToken: Ct);
        await updates.RunIfDueAsync(Ct);
        Assert.Equal(UpdateState.Ready, (await h.Client.GetUpdateStatusAsync(new GetUpdateStatusRequest(), cancellationToken: Ct)).State);
        Assert.Empty(installer.Started);

        Assert.Equal(RunState.Succeeded, (await h.WatchAsync(run.Id)).Finished.State);
        await updates.RunIfDueAsync(Ct);

        Assert.Single(installer.Started);
        Assert.Equal(UpdateState.Installing, (await h.Client.GetUpdateStatusAsync(new GetUpdateStatusRequest(), cancellationToken: Ct)).State);
    }

    [Fact]
    public async Task A_feed_signed_with_another_key_downloads_nothing()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        feed.Publish("99.0.0", SignedPackage, signWith: impostor);
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(A_feed_signed_with_another_key_downloads_nothing), feed, installer);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Contains("not signed by CashGear", status.Message, StringComparison.Ordinal);
        Assert.Equal(0, feed.Downloads);
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_feed_is_deleted()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", SignedPackage, sha256: new string('0', 64));
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(A_download_that_does_not_match_the_feed_is_deleted), feed, installer);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Contains("does not match", status.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Data, "updates"), "*.msi*"));
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task An_unsigned_package_is_refused()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", RandomNumberGenerator.GetBytes(64 * 1024));
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(An_unsigned_package_is_refused), feed, installer);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Contains("not signed", status.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Data, "updates"), "*.msi*"));
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task A_package_signed_by_someone_else_is_refused()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("99.0.0", SignedPackage);
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(A_package_signed_by_someone_else_is_refused), feed, installer, publisher: new string('A', 64));

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.Failed, status.State);
        Assert.Contains("not the CashGear publisher", status.Message, StringComparison.Ordinal);
        Assert.Empty(installer.Started);
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_up_to_date()
    {
        await using var feed = await FakeUpdateServer.StartAsync();
        feed.Publish("0.0.1", SignedPackage);
        var installer = new RecordingInstaller();
        await using var h = await StartAsync(nameof(The_same_or_an_older_version_is_up_to_date), feed, installer);

        var status = await h.Client.CheckForUpdatesAsync(new CheckForUpdatesRequest(), cancellationToken: Ct);

        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.NotNull(status.LastCheckedAt);
        Assert.Equal(0, feed.Downloads);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.9+abc", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "1.10.0-beta", false)]
    [InlineData("2.0", "1.99.99", true)]
    public void Versions_compare_numerically(string published, string installed, bool newer) =>
        Assert.Equal(newer, new UpdateManifest(published, "https://example.test/a.msi", new string('0', 64), 1, DateTimeOffset.UtcNow, null).IsNewerThan(installed));

    private static Task<AgentHarness> StartAsync(string name, FakeUpdateServer feed, RecordingInstaller installer, string? publisher = null) =>
        AgentHarness.StartAsync(name, o =>
        {
            o.UpdateManifestUrl = feed.ManifestUrl;
            o.UpdatePublicKeys = [feed.PublicKey];
            o.UpdatePublisherThumbprints = [publisher ?? SignerThumbprint];
            o.HousekeepingInterval = Timeout.InfiniteTimeSpan;
        }, services => services.AddSingleton<IUpdateInstaller>(installer));
}
