using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

public class JobEditorTests : UiTest
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Sets_up_a_new_destination_and_the_job()
    {
        await ConnectAsync();
        var cut = Render<JobEditor>();

        FillWhat(cut, excludes: "*.tmp\n\n  node_modules/  \n");
        await NextAsync(cut);
        Assert.True(cut.Find("#dest-create").HasAttribute("checked"));
        FillNewDestination(cut);
        await NextAsync(cut);
        cut.Find("#schedule-daily").Change(true);
        cut.Find("#daily-time").Change("14:30");
        cut.Find("#day-0").Change(false);
        cut.Find("#day-6").Change(false);
        await NextAsync(cut);
        cut.Find("#keep-latest").Change("5");
        await NextAsync(cut);
        await cut.Find("#save").ClickAsync(new());

        var created = Assert.Single(Agent.CreatedDestinations);
        Assert.Equal("Backup disk", created.Name);
        Assert.Equal(@"E:\Backups", created.Location);
        Assert.Equal(DestinationKind.FileSystem, created.Kind);
        Assert.Equal(Password, created.Password);

        var spec = Assert.Single(Agent.CreatedJobs);
        Assert.Equal(Agent.Destinations.Single().Id, spec.DestinationId);
        Assert.Equal("Documents", spec.Name);
        Assert.Equal(@"C:\Users\pat\Documents", spec.SourcePath);
        Assert.Equal(["*.tmp", "node_modules/"], spec.Excludes);
        Assert.Equal(14 * 60 + 30, spec.Schedule.Daily.MinuteOfDay);
        Assert.Equal([1, 2, 3, 4, 5], spec.Schedule.Daily.DaysOfWeek);
        Assert.Equal(5, spec.Retention.KeepLatest);
        Assert.Equal(7, spec.Retention.KeepDaily);
        Assert.True(spec.Enabled);
        Assert.Equal(Compression.Balanced, spec.Compression);

        Assert.True(Agent.Calls.ToList().IndexOf("CreateDestination") < Agent.Calls.ToList().IndexOf("CreateJob"));

        // A new destination ends with the Recovery Kit offer; Done goes to the dashboard.
        cut.WaitForElement("#recovery-kit-panel");
        Assert.NotEqual(Navigation.BaseUri, Navigation.Uri);
        await cut.Find("#done").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(Navigation.BaseUri, Navigation.Uri));
    }

    [Fact]
    public async Task Offers_a_recovery_kit_for_a_new_destination_with_the_password_only_if_asked()
    {
        await ConnectAsync();
        var cut = await ReviewNewJobAsync();
        await cut.Find("#save").ClickAsync(new());
        cut.WaitForElement("#recovery-kit-panel");

        Assert.False(cut.Find("#kit-include-password").HasAttribute("checked"));
        await cut.Find("#save-kit").ClickAsync(new());
        cut.Find("#kit-include-password").Change(true);
        await cut.Find("#save-kit").ClickAsync(new());

        Assert.Equal(2, Shell.SavedFiles.Count);
        Assert.All(Shell.SavedFiles, f => Assert.Equal("CashGear Recovery Kit - Backup disk.pdf", f.Name));
        var without = System.Text.Encoding.Latin1.GetString(Shell.SavedFiles[0].Content);
        var with = System.Text.Encoding.Latin1.GetString(Shell.SavedFiles[1].Content);
        Assert.DoesNotContain(Password, without, StringComparison.Ordinal);
        Assert.Contains("Write the password here", without, StringComparison.Ordinal);
        Assert.Contains(Password, with, StringComparison.Ordinal);
        Assert.Contains("Saved", cut.Find("#kit-saved").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_destination_needs_no_recovery_kit_step()
    {
        Agent.Destinations.Add(Destination());
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);

        await cut.Find("#save").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(Navigation.BaseUri, Navigation.Uri));
        Assert.Empty(cut.FindAll("#recovery-kit-panel"));
    }

    [Fact]
    public async Task The_new_password_must_be_typed_twice_the_same()
    {
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        FillNewDestination(cut, confirmation: Password + "!");

        await NextAsync(cut);

        Assert.Contains("do not match", cut.Find("#step-errors").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("#password"));
        Assert.Empty(Agent.CreatedDestinations);
    }

    [Fact]
    public async Task A_short_new_password_is_refused()
    {
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        FillNewDestination(cut, password: "short", confirmation: "short");

        await NextAsync(cut);

        Assert.Contains("at least 8 characters", cut.Find("#step-errors").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_plainly_that_a_lost_password_cannot_be_recovered()
    {
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);

        var warning = cut.Find("#password-warning").TextContent;
        Assert.Contains("cannot be recovered", warning, StringComparison.Ordinal);
        Assert.Equal("password", cut.Find("#password").GetAttribute("type"));
        Assert.Equal("password", cut.Find("#password-confirmation").GetAttribute("type"));

        // Connecting a backup made earlier asks for its password once.
        cut.Find("#dest-connect").Change(true);
        Assert.NotEmpty(cut.FindAll("#password"));
        Assert.Empty(cut.FindAll("#password-confirmation"));
    }

    [Fact]
    public async Task Connects_a_backup_made_earlier()
    {
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        cut.Find("#dest-connect").Change(true);
        cut.Find("#destination-name").Change("Old disk");
        cut.Find("#destination-location").Change(@"F:\CashGear");
        cut.Find("#password").Change("old password");
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        await cut.Find("#save").ClickAsync(new());

        var connected = Assert.Single(Agent.ConnectedDestinations);
        Assert.Equal(@"F:\CashGear", connected.Location);
        Assert.Equal("old password", connected.Password);
        Assert.Empty(Agent.CreatedDestinations);
        Assert.Equal(Agent.Destinations.Single().Id, Assert.Single(Agent.CreatedJobs).DestinationId);
    }

    [Fact]
    public async Task Keeps_at_least_the_latest_backup()
    {
        Agent.Destinations.Add(Destination());
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);

        cut.Find("#keep-latest").Change("0");
        await NextAsync(cut);

        Assert.Contains("Keep at least the latest backup", cut.Find("#step-errors").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("#keep-latest"));
        Assert.Empty(cut.FindAll("#save"));
    }

    [Fact]
    public async Task Uses_an_existing_destination_without_asking_for_a_password()
    {
        Agent.Destinations.Add(Destination());
        await ConnectAsync();
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);

        Assert.True(cut.Find("#dest-existing").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("#password"));
        await NextAsync(cut);
        cut.Find("#schedule-interval").Change(true);
        cut.Find("#interval-hours").Change("6");
        await NextAsync(cut);
        await NextAsync(cut);
        await cut.Find("#save").ClickAsync(new());

        Assert.Empty(Agent.CreatedDestinations);
        var spec = Assert.Single(Agent.CreatedJobs);
        Assert.Equal("dest-1", spec.DestinationId);
        Assert.Equal(6, spec.Schedule.Interval.Hours);
    }

    [Fact]
    public async Task An_agent_error_is_shown_and_a_retry_does_not_create_the_destination_twice()
    {
        await ConnectAsync();
        Agent.Failures["CreateJob"] = Error(AgentErrorKinds.AlreadyExists, "A job already backs up C:\\Users\\pat\\Documents to this destination.", Grpc.Core.StatusCode.AlreadyExists);
        var cut = await ReviewNewJobAsync();

        await cut.Find("#save").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("already backs up", cut.Find(".error-box").TextContent, StringComparison.Ordinal));
        Assert.Single(Agent.CreatedDestinations);

        Agent.Failures.Clear();
        await cut.Find("#save").ClickAsync(new());

        Assert.Single(Agent.CreatedDestinations);
        Assert.Single(Agent.CreatedJobs);
        Assert.Equal(2, Agent.CountOf("CreateJob"));
    }

    [Fact]
    public async Task A_declined_approval_says_nothing_was_changed()
    {
        await ConnectAsync();
        Agent.Failures["CreateDestination"] = Error(AgentErrorKinds.ElevationCanceled, "Administrator approval was not given, so nothing was changed.", Grpc.Core.StatusCode.Cancelled);
        var cut = await ReviewNewJobAsync();

        await cut.Find("#save").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("did not get administrator approval", cut.Find(".error-box").TextContent, StringComparison.Ordinal));
        Assert.Empty(Agent.CreatedJobs);
    }

    [Fact]
    public async Task Says_when_windows_will_ask_for_approval()
    {
        Agent.Status.CallerCanManage = false;
        await ConnectAsync();

        var cut = await ReviewNewJobAsync();

        Assert.Contains("administrator approval", cut.Find(".elevation-note").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_keeps_the_folder_and_destination_and_saves_the_changes()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(schedule: new Schedule { Daily = new DailySchedule { MinuteOfDay = 2 * 60 } }));
        await ConnectAsync();
        var cut = Render<JobEditor>(p => p.Add(e => e.JobId, "job-1"));

        cut.WaitForAssertion(() => Assert.Equal("Documents", cut.Find("#job-name").GetAttribute("value")));
        Assert.True(cut.Find("#source").HasAttribute("disabled"));
        await NextAsync(cut);
        Assert.Equal("02:00", cut.Find("#daily-time").GetAttribute("value"));
        cut.Find("#schedule-interval").Change(true);
        cut.Find("#interval-hours").Change("6");
        await NextAsync(cut);
        cut.Find("#keep-daily").Change("14");
        await NextAsync(cut);
        await cut.Find("#save").ClickAsync(new());

        var (id, spec) = Assert.Single(Agent.UpdatedJobs);
        Assert.Equal("job-1", id);
        Assert.Equal("dest-1", spec.DestinationId);
        Assert.Equal(@"C:\Users\pat\Documents", spec.SourcePath);
        Assert.Equal(6, spec.Schedule.Interval.Hours);
        Assert.Equal(14, spec.Retention.KeepDaily);
        Assert.Equal(10, spec.Retention.KeepLatest);
        Assert.Empty(Agent.CreatedJobs);
    }

    [Fact]
    public async Task Deleting_a_job_asks_first_and_says_its_backups_stay()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job());
        await ConnectAsync();
        var cut = Render<JobEditor>(p => p.Add(e => e.JobId, "job-1"));
        cut.WaitForElement("#job-name");
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);

        await cut.Find("#delete").ClickAsync(new());

        Assert.False(Agent.Called("DeleteJob:job-1"));
        Assert.Contains("backups stay", cut.Find("#delete-confirmation").TextContent, StringComparison.Ordinal);
        await cut.Find("#confirm-delete").ClickAsync(new());

        Assert.True(Agent.Called("DeleteJob:job-1"));
        cut.WaitForAssertion(() => Assert.Equal(Navigation.BaseUri, Navigation.Uri));
    }

    private async Task<IRenderedComponent<JobEditor>> ReviewNewJobAsync()
    {
        var cut = Render<JobEditor>();
        FillWhat(cut);
        await NextAsync(cut);
        FillNewDestination(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        await NextAsync(cut);
        cut.WaitForElement("#save");
        return cut;
    }

    private static void FillWhat(IRenderedComponent<JobEditor> cut, string excludes = "")
    {
        cut.WaitForElement("#job-name");
        cut.Find("#job-name").Change("Documents");
        cut.Find("#source").Change(@"C:\Users\pat\Documents");
        cut.Find("#excludes").Change(excludes);
    }

    private static void FillNewDestination(IRenderedComponent<JobEditor> cut, string password = Password, string confirmation = Password)
    {
        cut.Find("#dest-create").Change(true);
        cut.Find("#destination-name").Change("Backup disk");
        cut.Find("#destination-location").Change(@"E:\Backups");
        cut.Find("#password").Change(password);
        cut.Find("#password-confirmation").Change(confirmation);
    }

    private static Task NextAsync(IRenderedComponent<JobEditor> cut) => cut.Find("#next").ClickAsync(new());
}
