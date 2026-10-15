using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

public class DashboardTests : UiTest
{
    [Fact]
    public async Task Without_jobs_it_offers_to_set_up_the_first_backup()
    {
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("No backups yet", cut.Find("#empty-state").TextContent, StringComparison.Ordinal));
        Assert.NotEmpty(cut.FindAll("#empty-state a[href='jobs/new']"));
    }

    [Fact]
    public async Task Shows_the_last_successful_backup_the_last_run_and_the_next_one()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(nextRunAt: At(Now.AddDays(1))), BackupRun("run-1", RunState.Succeeded, Today(9, 30)));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Equal("today at 09:31", cut.Find("#job-job-1 .last-good").TextContent.Trim()));
        Assert.Equal("Succeeded", cut.Find("#job-job-1 .last-run .badge").TextContent.Trim());
        Assert.Contains("tomorrow at 12:00", cut.Find("#job-job-1 .next-run").TextContent, StringComparison.Ordinal);
        Assert.Contains("Backup disk", cut.Find("#job-job-1 .paths").TextContent, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\pat\Documents", cut.Find("#job-job-1 .paths").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_that_skipped_files_is_completed_with_warnings_never_a_success()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(),
            BackupRun("run-2", RunState.CompletedWithWarnings, Today(9, 30), failed: [new FailedItem { Path = "locked.pst", Error = "The file is in use." }]),
            BackupRun("run-1", RunState.Succeeded, At(Now.AddDays(-1).AddHours(-3))));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Equal("Completed with warnings", cut.Find("#job-job-1 .last-run .badge").TextContent.Trim()));
        var badge = cut.Find("#job-job-1 .last-run .badge");
        Assert.Contains("badge-warn", badge.ClassList);
        Assert.DoesNotContain("badge-ok", badge.ClassList);
        // The last successful backup is the older, complete one.
        Assert.Equal("yesterday at 09:01", cut.Find("#job-job-1 .last-good").TextContent.Trim());
        var failed = cut.Find("#job-job-1 .failed-items").TextContent;
        Assert.Contains("1 item could not be backed up", failed, StringComparison.Ordinal);
        Assert.Contains("locked.pst", failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_with_no_successful_backup_says_so()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Failed, Today(9, 30)));
        Agent.Runs["job-1"][0].Message = "The destination E:\\Backups is not reachable (disconnected drive or offline share).";
        Agent.Jobs[0].LastRun.Message = Agent.Runs["job-1"][0].Message;
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Equal("None yet", cut.Find("#job-job-1 .last-good").TextContent.Trim()));
        Assert.Equal("Failed", cut.Find("#job-job-1 .last-run .badge").TextContent.Trim());
        Assert.Contains("not reachable", cut.Find("#job-job-1 .run-message").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Back_up_now_starts_a_run_and_follows_its_progress_to_the_end()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Succeeded, At(Now.AddDays(-1))));
        await ConnectAsync();
        var cut = Render<Dashboard>();
        cut.WaitForElement("#job-job-1 .backup-now");

        await cut.Find("#job-job-1 .backup-now").ClickAsync(new());

        Assert.True(Agent.Called("RunJobNow:job-1"));
        cut.WaitForAssertion(() => Assert.True(Agent.Called("WatchRun:run-new")));
        Agent.Progress("run-new", new RunProgress
        {
            RunId = "run-new", State = RunState.Running, FilesProcessed = 10, BytesProcessed = 2048, EstimatedBytes = 4096, EstimatedFiles = 20,
        });
        cut.WaitForAssertion(() => Assert.Contains("10 files", cut.Find("#job-job-1 .progress-text").TextContent, StringComparison.Ordinal));
        Assert.Contains("width: 50%", cut.Find("#job-job-1 .progress .bar").GetAttribute("style"), StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("#job-job-1 button.cancel"));

        // The run ends: the dashboard reloads the job and shows the result.
        var finished = BackupRun("run-new", RunState.Succeeded, Today(12, 0));
        Agent.Runs["job-1"].Insert(0, finished);
        Agent.Jobs[0].LastRun = finished;
        Agent.Finish("run-new", finished);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#job-job-1 .progress")));
        Assert.Equal("today at 12:01", cut.Find("#job-job-1 .last-good").TextContent.Trim());
    }

    [Fact]
    public async Task A_running_backup_is_followed_and_can_be_canceled()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-9", RunState.Running, Today(11, 58)));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.True(Agent.Called("WatchRun:run-9")));
        cut.WaitForElement("#job-job-1 button.cancel");
        Assert.True(cut.Find("#job-job-1 .backup-now").HasAttribute("disabled"));

        await cut.Find("#job-job-1 button.cancel").ClickAsync(new());

        Assert.True(Agent.Called("CancelRun:run-9"));
    }

    [Fact]
    public async Task Back_up_now_waits_for_the_agent_to_be_ready()
    {
        Agent.Status.Ready = false;
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Succeeded, Today(9, 30)));
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForElement("#job-job-1 .backup-now");
        Assert.True(cut.Find("#job-job-1 .backup-now").HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_run_the_agent_refuses_shows_why_on_the_job()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(), BackupRun("run-1", RunState.Succeeded, Today(9, 30)));
        Agent.Failures["RunJobNow"] = Error(AgentErrorKinds.FailedPrecondition, "A backup of this job is already running.");
        await ConnectAsync();
        var cut = Render<Dashboard>();
        cut.WaitForElement("#job-job-1 .backup-now");

        await cut.Find("#job-job-1 .backup-now").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("already running", cut.Find("#job-job-1 .error-box").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_agent_it_explains_instead_of_listing()
    {
        Agent.Failures["GetStatus"] = Error(AgentErrorKinds.AgentNotRunning, "The CashGear Backup service is not running or did not answer.", Grpc.Core.StatusCode.Unavailable);
        Agent.Failures["ListJobs"] = Agent.Failures["GetStatus"];
        Agent.Failures["ListDestinations"] = Agent.Failures["GetStatus"];
        await ConnectAsync();

        var cut = Render<Dashboard>();

        cut.WaitForElement("#not-connected");
        Assert.Empty(cut.FindAll("#empty-state"));
        Assert.Empty(cut.FindAll(".error-box"));
    }
}
