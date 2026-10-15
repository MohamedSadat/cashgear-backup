using System.Security.Principal;
using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using AlertKind = CashGear.Backup.Contracts.V1.AlertKind;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.Agent.Tests;

/// <summary>Alerts, notification settings (the email password is write-only) and email.</summary>
[Trait("Category", "Integration")]
public class AlertsAndSettingsTests
{
    private const string MailPassword = "smtp-Secret-81942";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_without_a_restore_point_for_too_long_raises_an_alert_until_it_is_backed_up()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_job_without_a_restore_point_for_too_long_raises_an_alert_until_it_is_backed_up));
        RestoreToOriginalTests.WriteSource(h.Source);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        Assert.Empty(await AlertsAsync(h));

        await h.StopAgentAsync();
        await h.WithStateDbAsync(async db =>
        {
            var stored = await db.Jobs.FindAsync([Guid.Parse(job.Id)], Ct);
            stored!.CreatedAt = stored.CreatedAt.AddDays(-5);
            await db.SaveChangesAsync(Ct);
        });
        await h.StartAgentAsync();

        var alert = Assert.Single(await AlertsAsync(h));
        Assert.Equal(AlertKind.NoRecentBackup, alert.Kind);
        Assert.Equal(job.Id, alert.JobId);
        Assert.Contains("5 days", alert.Message, StringComparison.Ordinal);

        await RestoreToOriginalTests.BackUpAsync(h, job);
        Assert.Empty(await AlertsAsync(h));
    }

    [Fact]
    public async Task A_failed_backup_raises_an_alert_and_a_paused_job_raises_none()
    {
        await using var h = await AgentHarness.StartAsync(nameof(A_failed_backup_raises_an_alert_and_a_paused_job_raises_none));
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        Directory.Delete(h.Source, recursive: true);

        var failed = await h.RunNowAsync(job.Id);

        Assert.Equal(RunState.Failed, failed.State);
        var alert = Assert.Single(await AlertsAsync(h));
        Assert.Equal(AlertKind.BackupFailed, alert.Kind);
        Assert.Equal($"backup-failed:{failed.Id}", alert.Id);

        var spec = job.Spec.Clone();
        spec.Enabled = false;
        await h.Client.UpdateJobAsync(new UpdateJobRequest { Id = job.Id, Spec = spec }, cancellationToken: Ct);
        Assert.Empty(await AlertsAsync(h));
    }

    [Fact]
    public async Task Settings_have_defaults_and_the_email_password_is_write_only_and_never_stored_in_clear()
    {
        await using var h = await AgentHarness.StartAsync(nameof(Settings_have_defaults_and_the_email_password_is_write_only_and_never_stored_in_clear));

        var defaults = await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        Assert.Equal((3, 7, 5.0), (defaults.AlertAfterDays, defaults.VerifyEveryDays, defaults.VerifySamplePercent));
        Assert.False(defaults.Email.Enabled);

        var saved = await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = Settings(port: 2525, password: MailPassword) }, cancellationToken: Ct);
        Assert.Equal("", saved.Email.Password);
        Assert.True(saved.Email.PasswordSaved);
        Assert.Equal(2, saved.AlertAfterDays);

        // An update without a password keeps the saved one; clear_password removes it.
        var kept = await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = Settings(port: 2525, password: "") }, cancellationToken: Ct);
        Assert.True(kept.Email.PasswordSaved);
        var read = await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        Assert.Equal("", read.Email.Password);
        Assert.True(read.Email.PasswordSaved);
        Assert.Equal("mail.example.test", read.Email.SmtpHost);

        await h.StopAgentAsync();
        await h.WithStateDbAsync(async db =>
        {
            var all = string.Join("\n", await db.Settings.Select(s => s.Value).ToListAsync(Ct));
            Assert.DoesNotContain(MailPassword, all, StringComparison.Ordinal);
        });
        await h.StartAgentAsync();
        var cleared = Settings(port: 2525, password: "");
        cleared.Email.ClearPassword = true;
        Assert.False((await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = cleared }, cancellationToken: Ct)).Email.PasswordSaved);
        Assert.DoesNotContain(MailPassword, h.ReadLogs(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 7, 5, "", "")]
    [InlineData(3, 91, 5, "", "")]
    [InlineData(3, 7, 101, "", "")]
    [InlineData(3, 7, 5, "not-an-address", "")]
    [InlineData(3, 7, 5, "", "user-without-encryption")]
    public async Task Invalid_settings_are_refused(int alertDays, int verifyDays, double percent, string to, string usernameWithoutTls)
    {
        await using var h = await AgentHarness.StartAsync(nameof(Invalid_settings_are_refused));
        var settings = Settings(port: 2525, password: "pw-base-1234");
        settings.AlertAfterDays = alertDays;
        settings.VerifyEveryDays = verifyDays;
        settings.VerifySamplePercent = percent;
        if (to.Length > 0)
        {
            settings.Email.ToAddresses = to;
        }

        if (usernameWithoutTls.Length > 0)
        {
            settings.Email.Security = SmtpSecurity.None;
            settings.Email.Username = usernameWithoutTls;
            settings.Email.Password = "pw-12345678";
        }

        var ex = await Assert.ThrowsAsync<RpcException>(() => h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = settings }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("InvalidArgument", AgentHarness.ErrorKind(ex));
    }

    [Fact]
    public async Task Settings_changes_and_test_emails_need_an_elevated_administrator()
    {
        Assert.SkipWhen(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "The tests run elevated.");
        await using var h = await AgentHarness.StartAsync(nameof(Settings_changes_and_test_emails_need_an_elevated_administrator), o => o.AllowNonElevatedManage = false);

        var update = await Assert.ThrowsAsync<RpcException>(() => h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = Settings(2525, "pw-base-1234") }, cancellationToken: Ct).ResponseAsync);
        var test = await Assert.ThrowsAsync<RpcException>(() => h.Client.SendTestEmailAsync(new SendTestEmailRequest { Email = Settings(2525, "pw-base-1234").Email }, cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, update.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, test.StatusCode);
        Assert.NotNull(await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct));
    }

    [Fact]
    public async Task A_test_email_reaches_the_server_and_an_unreachable_server_is_reported()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await AgentHarness.StartAsync(nameof(A_test_email_reaches_the_server_and_an_unreachable_server_is_reported));

        await h.Client.SendTestEmailAsync(new SendTestEmailRequest { Email = Settings(smtp.Port, "", tls: false).Email }, cancellationToken: Ct);

        var mail = Assert.Single(smtp.Messages);
        Assert.Contains("<backup@example.test>", mail.From, StringComparison.Ordinal);
        Assert.Equal(["<pat@example.test>", "<it@example.test>"], mail.To);
        Assert.Contains("Subject: CashGear Backup: test email", mail.Data, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<RpcException>(() => h.Client.SendTestEmailAsync(new SendTestEmailRequest { Email = Settings(1, "", tls: false).Email }, cancellationToken: Ct).ResponseAsync);
        Assert.Equal("FailedPrecondition", AgentHarness.ErrorKind(ex));
    }

    [Fact]
    public async Task New_alerts_are_emailed_once()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await AgentHarness.StartAsync(nameof(New_alerts_are_emailed_once), o => o.HousekeepingInterval = Timeout.InfiniteTimeSpan);
        await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = Settings(smtp.Port, "", tls: false) }, cancellationToken: Ct);
        var job = await h.CreateJobAsync((await h.CreateDestinationAsync()).Id);
        Directory.Delete(h.Source, recursive: true);
        await h.RunNowAsync(job.Id);
        var housekeeping = h.App.Services.GetRequiredService<Housekeeping>();

        await housekeeping.RunOnceAsync(Ct);
        await housekeeping.RunOnceAsync(Ct);

        var mail = Assert.Single(smtp.Messages);
        Assert.Contains("Documents", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Subject: CashGear Backup: 1 problem needs your attention", mail.Data, StringComparison.Ordinal);

        // A second failure is a new alert.
        await h.RunNowAsync(job.Id);
        await housekeeping.RunOnceAsync(Ct);
        Assert.Equal(2, smtp.Messages.Count);
    }

    private static async Task<List<Alert>> AlertsAsync(AgentHarness h) =>
        [.. (await h.Client.ListAlertsAsync(new ListAlertsRequest(), cancellationToken: Ct)).Alerts];

    /// <summary>tls: STARTTLS with a username (and the password, or the saved one); otherwise a plain local relay without login.</summary>
    private static Settings Settings(int port, string password, bool tls = true) => new()
    {
        AlertAfterDays = 2,
        VerifyEveryDays = 7,
        VerifySamplePercent = 5,
        Email = new EmailSettings
        {
            Enabled = true,
            SmtpHost = port == 2525 ? "mail.example.test" : "127.0.0.1",
            SmtpPort = port,
            Security = tls ? SmtpSecurity.Starttls : SmtpSecurity.None,
            Username = tls ? "backup" : "",
            Password = password,
            FromAddress = "backup@example.test",
            ToAddresses = "pat@example.test; it@example.test",
        },
    };
}
