using Bunit;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

public class JobHistoryTests : UiTest
{
    [Fact]
    public async Task Lists_backups_and_restores_with_their_results()
    {
        var restore = new Run
        {
            Id = "restore-1", JobId = "job-1", Kind = RunKind.Restore, State = RunState.Succeeded, Trigger = RunTrigger.Manual,
            StartedAt = Today(11, 0), FinishedAt = Today(11, 2), Files = 3, RestoreTarget = @"C:\Users\pat\Restored\Documents",
        };
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job(),
            restore,
            BackupRun("run-2", RunState.CompletedWithWarnings, Today(9, 30), failed: [new FailedItem { Path = "locked.pst", Error = "in use" }]),
            BackupRun("run-1", RunState.Succeeded, At(Now.AddDays(-1))));
        await ConnectAsync();

        var cut = Render<JobHistory>(p => p.Add(h => h.JobId, "job-1"));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tr.run").Count));
        Assert.Contains("Documents", cut.Find("h1").TextContent, StringComparison.Ordinal);
        var rows = cut.FindAll("tr.run");
        Assert.Contains("Restore", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\pat\Restored\Documents", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("Completed with warnings", rows[1].QuerySelector(".badge")!.TextContent.Trim());
        Assert.Contains("locked.pst", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("Succeeded", rows[2].QuerySelector(".badge")!.TextContent.Trim());
        Assert.True(Agent.Called("ListRuns:job-1"));
    }

    [Fact]
    public async Task An_unknown_job_says_so()
    {
        await ConnectAsync();

        var cut = Render<JobHistory>(p => p.Add(h => h.JobId, "nope"));

        cut.WaitForAssertion(() => Assert.Contains("no longer exists", cut.Find(".error-box").TextContent, StringComparison.Ordinal));
    }
}
