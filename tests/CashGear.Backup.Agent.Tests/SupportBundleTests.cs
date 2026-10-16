using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CashGear.Backup.Agent.Support;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Agent.Tests;

[Trait("Category", "Integration")]
public class SupportBundleTests
{
    private const string MailPassword = "mail-Secret-Bundle-1";
    private const string MailTo = "owner@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_support_file_has_logs_and_a_summary_but_no_secrets()
    {
        await using var h = await AgentHarness.StartAsync(nameof(The_support_file_has_logs_and_a_summary_but_no_secrets));
        RestoreToOriginalTests.WriteSource(h.Source);
        var destination = await h.CreateDestinationAsync();
        var job = await h.CreateJobAsync(destination.Id);
        await h.RunNowAsync(job.Id);
        var settings = await h.Client.GetSettingsAsync(new GetSettingsRequest(), cancellationToken: Ct);
        settings.Email = new EmailSettings
        {
            SmtpHost = "mail.example.test", Security = SmtpSecurity.Starttls, Username = "backup", Password = MailPassword,
            FromAddress = "pc@example.test", ToAddresses = MailTo,
        };
        await h.Client.UpdateSettingsAsync(new UpdateSettingsRequest { Settings = settings }, cancellationToken: Ct);

        var bundle = await h.Client.CreateSupportBundleAsync(new CreateSupportBundleRequest(), cancellationToken: Ct);

        Assert.StartsWith("cashgear-support-", bundle.FileName, StringComparison.Ordinal);
        using var zip = new ZipArchive(new MemoryStream(bundle.Content.ToByteArray()));
        var entries = zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd());
        Assert.Contains("README.txt", entries.Keys);
        Assert.Contains(entries.Keys, name => name.StartsWith("logs/agent-", StringComparison.Ordinal));
        Assert.Contains(job.Id, string.Concat(entries.Where(e => e.Key.StartsWith("logs/", StringComparison.Ordinal)).Select(e => e.Value)), StringComparison.Ordinal);

        using var summary = JsonDocument.Parse(entries["summary.json"]);
        var root = summary.RootElement;
        Assert.Equal(job.Id, root.GetProperty("jobs")[0].GetProperty("Id").GetString());
        Assert.Equal(destination.Location, root.GetProperty("destinations")[0].GetProperty("Location").GetString());
        Assert.Equal("Succeeded", root.GetProperty("recentRuns")[0].GetProperty("state").GetString());
        Assert.True(root.GetProperty("settings").GetProperty("email").GetProperty("passwordSaved").GetBoolean());

        var everything = string.Concat(entries.Values);
        Assert.DoesNotContain(h.Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(MailPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(MailTo, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedPassword", everything, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("connect password=hunter2 now", "connect password=[removed] now")]
    [InlineData("{\"secretAccessKey\": \"abc def\"}", "{\"secretAccessKey\": [removed]}")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: [removed]")]
    [InlineData("token=abc; next", "token=[removed] next")]
    [InlineData("no secrets in C:\\Users\\pat\\Passwords folder", "no secrets in C:\\Users\\pat\\Passwords folder")]
    public void Log_lines_that_look_like_secrets_are_cut(string line, string expected) =>
        Assert.Equal(expected, SupportBundleService.Scrub(line));
}
