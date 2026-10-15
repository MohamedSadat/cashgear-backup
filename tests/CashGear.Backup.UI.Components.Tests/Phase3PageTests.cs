using System.Text;
using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

/// <summary>Restore timeline, search and restore to the original location.</summary>
public class RestoreWizardTests : UiTest
{
    public RestoreWizardTests()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job());
        Agent.Snapshots["job-1"] =
        [
            new Snapshot { Id = "s3", JobId = "job-1", StartTime = Today(11, 0), EndTime = Today(11, 1), Files = 3, Bytes = 1000, Complete = true },
            new Snapshot { Id = "s2", JobId = "job-1", StartTime = Today(9, 30), EndTime = Today(9, 31), Files = 3, Bytes = 1000, Complete = true },
            new Snapshot { Id = "s1", JobId = "job-1", StartTime = At(Now.AddDays(-1)), EndTime = At(Now.AddDays(-1).AddMinutes(1)), Files = 2, Bytes = 900, Complete = true },
        ];
        Agent.Entries[("s3", "")] =
        [
            new SnapshotEntry { Name = "docs", Path = "docs", Kind = EntryKind.Directory },
            new SnapshotEntry { Name = "top.txt", Path = "top.txt", Kind = EntryKind.File, Size = 3 },
        ];
        Agent.Entries[("s3", "docs")] = [new SnapshotEntry { Name = "a.txt", Path = "docs/a.txt", Kind = EntryKind.File, Size = 5 }];
        Agent.SearchResults[("s3", "a.txt")] = [new SnapshotEntry { Name = "a.txt", Path = "docs/a.txt", Kind = EntryKind.File, Size = 5, ModifiedAt = Today(8, 0) }];
    }

    [Fact]
    public async Task Restore_points_are_grouped_by_day()
    {
        await ConnectAsync();

        var cut = Render<Restore>(p => p.Add(r => r.JobId, "job-1"));

        cut.WaitForAssertion(() => Assert.Equal(["Today", "Yesterday"], cut.FindAll(".snapshots tr.day").Select(d => d.TextContent.Trim())));
        var rows = cut.FindAll(".snapshots tr");
        Assert.Equal(["day", "snapshot", "snapshot", "day", "snapshot"], rows.Skip(1).Select(r => r.ClassName));
        Assert.Contains("11:01", rows[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_finds_a_file_and_it_can_be_restored_on_its_own()
    {
        await ConnectAsync();
        var cut = await OpenNewestAsync();

        cut.Find("#search").Change("a.txt");
        await cut.Find("#search-go").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("docs/a.txt", cut.Find(".search-results").TextContent, StringComparison.Ordinal));
        Assert.True(Agent.Called("SearchSnapshot:s3/a.txt"));
        Assert.Contains("1 found", cut.Find("#search-status").TextContent, StringComparison.Ordinal);
        cut.Find(".search-results input.pick").Change(true);
        await cut.Find("#start-restore").ClickAsync(new());

        Assert.Equal("docs/a.txt", Assert.Single(Agent.Restores).Path);
    }

    [Fact]
    public async Task Show_folder_opens_the_found_item_in_the_browser()
    {
        await ConnectAsync();
        var cut = await OpenNewestAsync();
        cut.Find("#search").Change("a.txt");
        await cut.Find("#search-go").ClickAsync(new());
        cut.WaitForElement(".search-results button.show-folder");

        await cut.Find(".search-results button.show-folder").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(["a.txt"], cut.FindAll(".entry .name").Select(e => e.TextContent.Trim())));
        Assert.Empty(cut.FindAll(".search-results"));
    }

    [Fact]
    public async Task Restoring_back_to_where_it_was_keeps_both_and_asks_for_no_folder()
    {
        await ConnectAsync();
        var cut = await OpenNewestAsync();

        cut.Find("#to-original").Change(true);

        Assert.Empty(cut.FindAll("#restore-target"));
        var note = cut.Find("#original-note").TextContent;
        Assert.Contains("kept", note, StringComparison.Ordinal);
        Assert.Contains("Nothing is replaced", note, StringComparison.Ordinal);
        await cut.Find("#start-restore").ClickAsync(new());

        var request = Assert.Single(Agent.Restores);
        Assert.Equal(RestoreLocation.Original, request.Location);
        Assert.Equal("", request.TargetFolder);
        Agent.Finish("restore-new", new Run
        {
            Id = "restore-new", JobId = "job-1", Kind = RunKind.Restore, State = RunState.Succeeded, Files = 3,
            RestoreTarget = @"C:\Users\pat\Documents",
            Message = "Restored into C:\\Users\\pat\\Documents. 1 file was already there and different: they were kept, and the restored copies are next to them.",
        });
        cut.WaitForAssertion(() => Assert.Contains("already there and different", cut.Find("#restore-result").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_new_folder_choice_stays_the_default()
    {
        await ConnectAsync();
        var cut = await OpenNewestAsync();

        Assert.True(cut.Find("#to-new-folder").HasAttribute("checked"));
        await cut.Find("#start-restore").ClickAsync(new());

        Assert.Equal(RestoreLocation.NewFolder, Assert.Single(Agent.Restores).Location);
    }

    private async Task<IRenderedComponent<Restore>> OpenNewestAsync()
    {
        var cut = Render<Restore>(p => p.Add(r => r.JobId, "job-1"));
        cut.WaitForElement(".snapshot button.choose");
        await cut.Find(".snapshot button.choose").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".entry").Count));
        return cut;
    }
}

/// <summary>Alerts and checks on the dashboard and in the history.</summary>
public class ChecksAndAlertsTests : UiTest
{
    [Fact]
    public async Task Alerts_show_at_the_top_of_the_dashboard()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Failed, Today(9, 0)));
        Agent.Alerts.Add(new Alert { Id = "backup-failed:run-1", Kind = AlertKind.BackupFailed, JobId = "job-1", JobName = "Documents", Message = "The destination is not reachable.", Since = Today(9, 1) });
        Agent.Alerts.Add(new Alert { Id = "no-recent-backup:job-1:none", Kind = AlertKind.NoRecentBackup, JobId = "job-1", JobName = "Documents", Message = "The last backup was 4 days ago.", Since = Today(8, 0) });
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#alerts .alert").Count));
        var first = cut.FindAll("#alerts .alert")[0];
        Assert.Contains("Documents", first.TextContent, StringComparison.Ordinal);
        Assert.Contains("not reachable", first.TextContent, StringComparison.Ordinal);
        Assert.Contains("alert-error", first.ClassList);
        Assert.Contains("alert-warn", cut.FindAll("#alerts .alert")[1].ClassList);
    }

    [Fact]
    public async Task Without_alerts_there_is_no_panel()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Succeeded, Today(9, 0)));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForElement("#job-job-1");
        Assert.Empty(cut.FindAll("#alerts"));
    }

    [Fact]
    public async Task Check_backups_runs_a_check_and_shows_its_result()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Succeeded, Today(9, 0)));
        await ConnectAsync();
        var cut = Render<Dashboard>();
        cut.WaitForElement("#job-job-1 .verify-now");
        Assert.Contains("Not checked yet", cut.Find("#job-job-1 .last-check").TextContent, StringComparison.Ordinal);

        await cut.Find("#job-job-1 .verify-now").ClickAsync(new());

        Assert.True(Agent.Called("VerifyJobNow:job-1"));
        cut.WaitForAssertion(() => Assert.Contains("Checking", cut.Find("#job-job-1 .last-check").TextContent, StringComparison.Ordinal));
        Assert.True(cut.Find("#job-job-1 .verify-now").HasAttribute("disabled"));
        var finished = new Run { Id = "verify-new", JobId = "job-1", Kind = RunKind.Verify, State = RunState.Succeeded, StartedAt = Today(12, 0), FinishedAt = Today(12, 2), Files = 6 };
        Agent.Runs["job-1"].Insert(0, finished);
        Agent.Finish("verify-new", finished);

        cut.WaitForAssertion(() => Assert.Contains("No problems found", cut.Find("#job-job-1 .last-check").TextContent, StringComparison.Ordinal));
        Assert.Contains("today at 12:02", cut.Find("#job-job-1 .last-check").TextContent, StringComparison.Ordinal);
        // A check is not a backup.
        Assert.Equal("today at 09:01", cut.Find("#job-job-1 .last-good").TextContent.Trim());
    }

    [Fact]
    public async Task A_failed_check_is_shown_as_a_problem()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(),
            new Run { Id = "verify-1", JobId = "job-1", Kind = RunKind.Verify, State = RunState.Failed, StartedAt = Today(10, 0), FinishedAt = Today(10, 5), Message = "The check found 2 problem(s)." },
            BackupRun("run-1", RunState.Succeeded, Today(9, 0)));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("Problems found", cut.Find("#job-job-1 .last-check").TextContent, StringComparison.Ordinal));
        Assert.Contains("badge-error", cut.Find("#job-job-1 .last-check .badge").ClassList);
    }

    [Fact]
    public async Task History_shows_checks()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(),
            new Run { Id = "verify-1", JobId = "job-1", Kind = RunKind.Verify, State = RunState.Succeeded, Trigger = RunTrigger.Scheduled, StartedAt = Today(10, 0), FinishedAt = Today(10, 5), Files = 12, Message = "No problems found." },
            BackupRun("run-1", RunState.Succeeded, Today(9, 0)));
        await ConnectAsync();

        var cut = Render<JobHistory>(p => p.Add(h => h.JobId, "job-1"));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tr.run").Count));
        Assert.Contains("Check", cut.FindAll("tr.run")[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("No problems found", cut.FindAll("tr.run")[0].TextContent, StringComparison.Ordinal);
    }
}

public class SettingsPageTests : UiTest
{
    [Fact]
    public async Task Loads_the_settings_and_saves_changes_without_echoing_the_password()
    {
        Agent.Settings.Email = new EmailSettings
        {
            Enabled = true, SmtpHost = "smtp.example.test", SmtpPort = 587, Security = SmtpSecurity.Starttls, Username = "backup",
            PasswordSaved = true, FromAddress = "backup@example.test", ToAddresses = "pat@example.test",
        };
        await ConnectAsync();
        var cut = Render<SettingsPage>();
        cut.WaitForElement("#alert-days");

        Assert.Equal("3", cut.Find("#alert-days").GetAttribute("value"));
        Assert.Equal("smtp.example.test", cut.Find("#smtp-host").GetAttribute("value"));
        Assert.Contains("saved", cut.Find("#smtp-password").GetAttribute("placeholder"), StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrEmpty(cut.Find("#smtp-password").GetAttribute("value")));

        cut.Find("#alert-days").Change("5");
        cut.Find("#verify-percent").Change("10");
        await cut.Find("#save-settings").ClickAsync(new());

        var saved = Assert.Single(Agent.SavedSettings);
        Assert.Equal(5, saved.AlertAfterDays);
        Assert.Equal(10, saved.VerifySamplePercent);
        Assert.Equal("", saved.Email.Password);
        cut.WaitForAssertion(() => Assert.Contains("Saved", cut.Find("#settings-saved").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_typed_password_is_sent_once_and_the_field_is_cleared()
    {
        await ConnectAsync();
        var cut = Render<SettingsPage>();
        cut.WaitForElement("#smtp-password");
        cut.Find("#email-enabled").Change(true);
        cut.Find("#smtp-host").Change("smtp.example.test");
        cut.Find("#smtp-username").Change("backup");
        cut.Find("#smtp-password").Change("mail-Secret-1");
        cut.Find("#mail-from").Change("backup@example.test");
        cut.Find("#mail-to").Change("pat@example.test");

        await cut.Find("#save-settings").ClickAsync(new());

        Assert.Equal("mail-Secret-1", Assert.Single(Agent.SavedSettings).Email.Password);
        cut.WaitForAssertion(() => Assert.True(string.IsNullOrEmpty(cut.Find("#smtp-password").GetAttribute("value"))));
        Assert.DoesNotContain("mail-Secret-1", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_a_test_email_with_the_settings_on_the_page()
    {
        await ConnectAsync();
        var cut = Render<SettingsPage>();
        cut.WaitForElement("#smtp-host");
        cut.Find("#smtp-host").Change("smtp.example.test");
        cut.Find("#mail-from").Change("backup@example.test");
        cut.Find("#mail-to").Change("pat@example.test");

        await cut.Find("#send-test").ClickAsync(new());

        Assert.Equal("smtp.example.test", Assert.Single(Agent.TestEmails).SmtpHost);
        cut.WaitForAssertion(() => Assert.Contains("sent", cut.Find("#test-result").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_change_shows_why()
    {
        Agent.Failures["UpdateSettings"] = Error(AgentErrorKinds.InvalidArgument, "Enter the mail server's name, for example smtp.example.com.", Grpc.Core.StatusCode.InvalidArgument);
        await ConnectAsync();
        var cut = Render<SettingsPage>();
        cut.WaitForElement("#save-settings");

        await cut.Find("#save-settings").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("mail server", cut.Find(".error-box").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_recovery_kit_can_be_saved_for_each_destination_without_its_password()
    {
        Agent.Destinations.Add(Destination());
        await ConnectAsync();
        var cut = Render<SettingsPage>();
        cut.WaitForElement(".recovery-kit");

        await cut.Find(".recovery-kit").ClickAsync(new());

        var (name, content) = Assert.Single(Shell.SavedFiles);
        Assert.Equal("CashGear Recovery Kit - Backup disk.pdf", name);
        var text = Encoding.Latin1.GetString(content);
        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("Write the password here", text, StringComparison.Ordinal);
        Assert.Contains("cg-0123456789abcdef", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_when_windows_will_ask_for_approval()
    {
        Agent.Status.CallerCanManage = false;
        await ConnectAsync();

        var cut = Render<SettingsPage>();

        cut.WaitForElement("#save-settings");
        Assert.NotEmpty(cut.FindAll(".elevation-note"));
    }
}
