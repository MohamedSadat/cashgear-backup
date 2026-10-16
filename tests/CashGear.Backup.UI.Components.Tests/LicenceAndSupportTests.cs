using Bunit;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

/// <summary>Licence, updates, support file and the About page.</summary>
public class LicenceAndSupportTests : UiTest
{
    [Fact]
    public async Task A_key_is_activated_from_settings_and_not_kept_on_the_page()
    {
        Agent.Licence = FakeAgentClient.FreeLicence();
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Free edition", cut.Find("#licence-summary").TextContent, StringComparison.Ordinal));

        cut.Find("#licence-key").Change("  CGB-GOOD-1234 ");
        await cut.Find("#activate-licence").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Pro", cut.Find("#licence-summary").TextContent, StringComparison.Ordinal));
        Assert.Contains("key ending 1234", cut.Find("#licence-summary").TextContent, StringComparison.Ordinal);
        Assert.Equal("", cut.Find("#licence-key").GetAttribute("value"));
        Assert.Contains("ActivateLicence", Agent.Calls);
    }

    [Fact]
    public async Task A_refused_key_says_why_and_is_cleared()
    {
        Agent.Licence = FakeAgentClient.FreeLicence();
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#licence-key"));

        cut.Find("#licence-key").Change("CGB-NOPE");
        await cut.Find("#activate-licence").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("does not exist", cut.Find("#licence .error-box").TextContent, StringComparison.Ordinal));
        Assert.Equal("", cut.Find("#licence-key").GetAttribute("value"));
        Assert.Contains("Free edition", cut.Find("#licence-summary").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_the_licence_asks_first()
    {
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#remove-licence"));

        await cut.Find("#remove-licence").ClickAsync(new());
        Assert.DoesNotContain("RemoveLicence", Agent.Calls);
        Assert.Contains("only the first backup keeps running", cut.Find("#licence .actions").TextContent, StringComparison.Ordinal);
        await cut.Find("#remove-licence-confirm").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("Free edition", cut.Find("#licence-summary").TextContent, StringComparison.Ordinal));
        Assert.Contains("RemoveLicence", Agent.Calls);
        Assert.Empty(cut.FindAll("#remove-licence"));
    }

    [Fact]
    public void The_free_edition_cannot_turn_on_email_alerts()
    {
        Agent.Licence = FakeAgentClient.FreeLicence();
        var cut = Render<SettingsPage>();

        cut.WaitForAssertion(() => Assert.True(cut.Find("#email-enabled").HasAttribute("disabled")));
        Assert.Contains("Pro licence", cut.Find("#email-needs-pro").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LicenceState.Lapsed, "alert-error")]
    [InlineData(LicenceState.OfflineGrace, "alert-warn")]
    public void The_dashboard_says_when_the_licence_needs_attention(LicenceState state, string tone)
    {
        Agent.Licence = new LicenceStatus { Edition = Edition.Pro, State = state, Message = "The licence server has not been reached for 30 days." };
        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("not been reached", cut.Find("#licence-banner").TextContent, StringComparison.Ordinal));
        Assert.Contains(tone, cut.Find("#licence-banner").ClassList);
        Assert.Equal("settings", cut.Find("#licence-banner a").GetAttribute("href"));
    }

    [Fact]
    public void An_active_licence_shows_no_banner()
    {
        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("GetLicence", Agent.Calls));
        Assert.Empty(cut.FindAll("#licence-banner"));
    }

    [Fact]
    public void The_free_edition_offers_no_second_backup()
    {
        Agent.Licence = FakeAgentClient.FreeLicence();
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job());

        var cut = Render<JobEditor>();

        cut.WaitForAssertion(() => Assert.Contains("one folder", cut.Find("#free-limit").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#source"));
    }

    [Fact]
    public async Task The_free_edition_offers_folders_and_shares_but_not_sftp_or_cloud()
    {
        Agent.Licence = FakeAgentClient.FreeLicence();
        var cut = Render<JobEditor>();
        cut.WaitForElement("#job-name");
        cut.Find("#job-name").Change("Documents");
        cut.Find("#source").Change(@"C:\Users\pat\Documents");
        await cut.Find("#next").ClickAsync(new());
        cut.Find("#dest-create").Change(true);

        Assert.False(cut.Find("#dest-type-folder").HasAttribute("disabled"));
        Assert.False(cut.Find("#dest-type-share").HasAttribute("disabled"));
        Assert.True(cut.Find("#dest-type-sftp").HasAttribute("disabled"));
        Assert.True(cut.Find("#dest-type-s3").HasAttribute("disabled"));
        Assert.Contains("Pro licence", cut.Find("#cloud-needs-pro").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updates_can_be_checked_from_settings()
    {
        Agent.OnCheckForUpdates = () => new UpdateStatus { CurrentVersion = "1.0.0", AvailableVersion = "1.1.0", State = UpdateState.Ready };
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("1.0.0 is the latest", cut.Find("#update-status").TextContent, StringComparison.Ordinal));

        await cut.Find("#check-updates").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("1.1.0 is ready", cut.Find("#update-status").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Before_the_first_check_it_does_not_claim_to_be_the_latest()
    {
        Agent.Update = new UpdateStatus { CurrentVersion = "1.0.0", State = UpdateState.UpToDate };
        var cut = Render<SettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("not checked for updates yet", cut.Find("#update-status").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_update_source_there_is_nothing_to_check()
    {
        Agent.Update = new UpdateStatus { CurrentVersion = "1.0.0", State = UpdateState.NotConfigured };
        var cut = Render<SettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("not set up", cut.Find("#update-status").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#check-updates"));
    }

    [Fact]
    public async Task A_support_file_is_saved_where_the_user_chooses()
    {
        Shell.SaveAnswer = @"C:\Users\pat\Documents\cashgear-support.zip";
        var cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => cut.Find("#support-bundle"));

        await cut.Find("#support-bundle").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("cashgear-support.zip", cut.Find("#support-bundle-saved").TextContent, StringComparison.Ordinal));
        var saved = Assert.Single(Shell.SavedFiles);
        Assert.Equal("cashgear-support.zip", saved.Name);
        Assert.Equal([1, 2, 3], saved.Content);
    }

    [Fact]
    public async Task About_shows_the_versions_and_the_notices()
    {
        await ConnectAsync();
        var cut = Render<About>();

        cut.WaitForAssertion(() => Assert.Contains("Apache License", cut.Find("#notices").TextContent, StringComparison.Ordinal));
        Assert.Contains("App version 1.2.3", cut.Find("#about-versions").TextContent, StringComparison.Ordinal);
        Assert.Contains("service version 1.0.0", cut.Find("#about-versions").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void About_says_when_the_notices_are_missing()
    {
        Shell.Notices = "";
        var cut = Render<About>();

        cut.WaitForAssertion(() => cut.Find("#notices-missing"));
    }
}
