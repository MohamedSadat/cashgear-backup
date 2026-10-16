using System.Security.Cryptography;
using System.Text.Json;
using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using LicenceState = CashGear.Backup.Contracts.V1.LicenceState;

namespace CashGear.Backup.Agent.Tests;

/// <summary>Editions and licences. Restores never depend on the licence.</summary>
[Trait("Category", "Integration")]
public class LicensingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_free_edition_allows_one_job_on_a_folder_and_no_cloud_or_email()
    {
        await using var h = await AgentHarness.StartAsync(nameof(The_free_edition_allows_one_job_on_a_folder_and_no_cloud_or_email), o => o.LicenceEnforcement = true);

        var licence = await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct);
        Assert.Equal((Edition.Free, LicenceState.Free, 1, false, false, true),
            (licence.Edition, licence.State, licence.MaxJobs, licence.CloudDestinations, licence.EmailAlerts, licence.BackupsAllowed));

        var destination = await h.CreateDestinationAsync();
        await h.CreateJobAsync(destination.Id);
        Directory.CreateDirectory(Path.Combine(h.Root, "pictures"));
        var second = await Assert.ThrowsAsync<RpcException>(() => h.Client.CreateJobAsync(new CreateJobRequest
        {
            Spec = AgentHarness.Spec(destination.Id, Path.Combine(h.Root, "pictures")),
        }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("LicenceRequired", AgentHarness.ErrorKind(second));

        var sftp = new SftpSettings { Host = "nas.example.test", Username = "u", Password = "pw-12345678", Path = "/b", KnownHosts = { "nas.example.test ssh-ed25519 AAAA" } };
        var cloud = await Assert.ThrowsAsync<RpcException>(() => h.Client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "NAS", Kind = Contracts.V1.DestinationKind.Sftp, Password = h.Password, Sftp = sftp,
        }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("LicenceRequired", AgentHarness.ErrorKind(cloud));

        var settings = await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        settings.Email = new EmailSettings { Enabled = true, SmtpHost = "127.0.0.1", Security = SmtpSecurity.None, FromAddress = "a@example.test", ToAddresses = "b@example.test" };
        var email = await Assert.ThrowsAsync<RpcException>(() => h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = settings }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("LicenceRequired", AgentHarness.ErrorKind(email));
    }

    [Fact]
    public async Task Activating_a_key_unlocks_pro_and_removing_it_returns_to_free()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        await using var h = await StartLicensedAsync(nameof(Activating_a_key_unlocks_pro_and_removing_it_returns_to_free), server);

        var wrong = await Assert.ThrowsAsync<RpcException>(() => h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = "CGB-NOPE" }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("InvalidArgument", AgentHarness.ErrorKind(wrong));
        Assert.Contains("does not exist", wrong.Status.Detail, StringComparison.Ordinal);

        var licence = await h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = FakeLicenceServer.ValidKey }, cancellationToken: Ct);
        Assert.Equal((Edition.Pro, LicenceState.Active, "ABCD", 0, true, true), (licence.Edition, licence.State, licence.KeyHint, licence.MaxJobs, licence.CloudDestinations, licence.EmailAlerts));

        var destination = await h.CreateDestinationAsync();
        await h.CreateJobAsync(destination.Id);
        Directory.CreateDirectory(Path.Combine(h.Root, "pictures"));
        await h.Client.CreateJobAsync(new CreateJobRequest { Spec = AgentHarness.Spec(destination.Id, Path.Combine(h.Root, "pictures")) }, cancellationToken: Ct);

        Assert.Equal(LicenceState.Free, (await h.Client.RemoveLicenceAsync(new RemoveLicenceRequest(), cancellationToken: Ct)).State);
        Assert.DoesNotContain(FakeLicenceServer.ValidKey, h.ReadLogs(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_not_accepted()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        server.SignWith = impostor;
        await using var h = await StartLicensedAsync(nameof(A_token_signed_with_another_key_is_not_accepted), server);

        var ex = await Assert.ThrowsAsync<RpcException>(() => h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = FakeLicenceServer.ValidKey }, cancellationToken: Ct).ResponseAsync);

        Assert.Equal("InvalidArgument", AgentHarness.ErrorKind(ex));
        Assert.Equal(LicenceState.Free, (await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct)).State);
    }

    [Fact]
    public async Task Without_the_licence_server_backups_run_through_the_grace_period_then_pause_but_restores_still_work()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        await using var h = await StartLicensedAsync(nameof(Without_the_licence_server_backups_run_through_the_grace_period_then_pause_but_restores_still_work), server);
        await h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = FakeLicenceServer.ValidKey }, cancellationToken: Ct);
        RestoreToOriginalTests.WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        var snapshot = await RestoreToOriginalTests.BackUpAsync(h, job);
        var machine = (await h.Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct)).MachineIdentity;

        await StoreTokenAsync(h, server.Token(machine, DateTimeOffset.UtcNow.AddDays(-10)));
        var grace = await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct);
        Assert.Equal(LicenceState.OfflineGrace, grace.State);
        Assert.True(grace.BackupsAllowed);
        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(job.Id)).State);

        await StoreTokenAsync(h, server.Token(machine, DateTimeOffset.UtcNow.AddDays(-31)));
        var lapsed = await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct);
        Assert.Equal(LicenceState.Lapsed, lapsed.State);
        Assert.False(lapsed.BackupsAllowed);
        var paused = await h.RunNowAsync(job.Id);
        Assert.Equal(RunState.Failed, paused.State);
        Assert.Contains("paused", paused.Message, StringComparison.Ordinal);

        var restore = await h.Client.StartRestoreAsync(new StartRestoreRequest
        {
            JobId = job.Id, SnapshotId = snapshot.Id, TargetFolder = Path.Combine(h.Restores, "lapsed"),
        }, cancellationToken: Ct);
        Assert.Equal(RunState.Succeeded, (await h.WatchAsync(restore.Id)).Finished.State);
    }

    [Fact]
    public async Task The_daily_refresh_renews_the_licence_and_a_revoked_one_lapses()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        await using var h = await StartLicensedAsync(nameof(The_daily_refresh_renews_the_licence_and_a_revoked_one_lapses), server, o => o.HousekeepingInterval = Timeout.InfiniteTimeSpan);
        await h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = FakeLicenceServer.ValidKey }, cancellationToken: Ct);
        var machine = (await h.Client.GetStatusAsync(new GetStatusRequest(), cancellationToken: Ct)).MachineIdentity;
        await StoreTokenAsync(h, server.Token(machine, DateTimeOffset.UtcNow.AddDays(-10)));

        await h.App.Services.GetRequiredService<Housekeeping>().RunOnceAsync(Ct);

        Assert.Equal(1, server.Refreshes);
        Assert.Equal(LicenceState.Active, (await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct)).State);

        server.Revoked = true;
        await StoreTokenAsync(h, server.Token(machine, DateTimeOffset.UtcNow.AddDays(-2)));
        await h.App.Services.GetRequiredService<Housekeeping>().RunOnceAsync(Ct);

        var revoked = await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct);
        Assert.Equal(LicenceState.Lapsed, revoked.State);
        Assert.Contains("cancelled", revoked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_left_by_a_silent_install_is_activated_at_start()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        var name = nameof(A_key_left_by_a_silent_install_is_activated_at_start);
        await using var h = await StartLicensedAsync(name, server);
        await h.StopAgentAsync();
        var keyFile = Path.Combine(h.Data, "licence.ini");
        await File.WriteAllTextAsync(keyFile, $"[Licence]\r\nKey={FakeLicenceServer.ValidKey}\r\n", Ct);

        await h.StartAgentAsync();

        Assert.Equal(Edition.Pro, (await h.Client.GetLicenceAsync(new GetLicenceRequest(), cancellationToken: Ct)).Edition);
        Assert.False(File.Exists(keyFile));
    }

    [Fact]
    public async Task After_a_downgrade_only_the_first_job_backs_up()
    {
        await using var server = await FakeLicenceServer.StartAsync();
        await using var h = await StartLicensedAsync(nameof(After_a_downgrade_only_the_first_job_backs_up), server);
        await h.Client.ActivateLicenceAsync(new ActivateLicenceRequest { Key = FakeLicenceServer.ValidKey }, cancellationToken: Ct);
        RestoreToOriginalTests.WriteSource(h.Source);
        var destination = await h.CreateDestinationAsync();
        var first = await h.CreateJobAsync(destination.Id);
        var otherSource = Path.Combine(h.Root, "pictures");
        Directory.CreateDirectory(otherSource);
        await File.WriteAllTextAsync(Path.Combine(otherSource, "p.txt"), "p", Ct);
        var second = await h.Client.CreateJobAsync(new CreateJobRequest { Spec = AgentHarness.Spec(destination.Id, otherSource) }, cancellationToken: Ct);

        await h.Client.RemoveLicenceAsync(new RemoveLicenceRequest(), cancellationToken: Ct);

        Assert.Equal(RunState.Succeeded, (await h.RunNowAsync(first.Id)).State);
        var blocked = await h.RunNowAsync(second.Id);
        Assert.Equal(RunState.Failed, blocked.State);
        Assert.Contains("Free edition", blocked.Message, StringComparison.Ordinal);
    }

    private static Task<AgentHarness> StartLicensedAsync(string name, FakeLicenceServer server, Action<AgentOptions>? more = null) =>
        AgentHarness.StartAsync(name, o =>
        {
            o.LicenceEnforcement = true;
            o.LicenceServerUrl = server.Url;
            o.LicencePublicKeys = [server.PublicKey];
            more?.Invoke(o);
        });

    /// <summary>Replaces the stored token (as if it had been issued at another time); the agent is restarted around it.</summary>
    private static async Task StoreTokenAsync(AgentHarness h, string token)
    {
        await h.StopAgentAsync();
        await h.WithStateDbAsync(async db =>
        {
            var record = await db.Settings.FindAsync(["Licence"], Ct);
            var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(record!.Value)!;
            var updated = new Dictionary<string, object?>
            {
                ["Token"] = token,
                ["KeyHint"] = stored["KeyHint"].GetString(),
                ["LastCheckedAt"] = DateTimeOffset.UtcNow.AddDays(-2),
            };
            record.Value = JsonSerializer.Serialize(updated);
            await db.SaveChangesAsync(Ct);
        });
        await h.StartAgentAsync();
    }
}
