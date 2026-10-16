using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

public class RestoreTests : UiTest
{
    private const string DefaultTarget = @"C:\Users\pat\Restored\Documents 2026-10-15 0931";

    public RestoreTests()
    {
        Agent.Destinations.Add(Destination());
        Agent.AddJob(Job());
        Agent.Snapshots["job-1"] =
        [
            new Snapshot { Id = "s2", JobId = "job-1", StartTime = Today(9, 30), EndTime = Today(9, 31), Files = 3, Bytes = 1000, Complete = true },
            new Snapshot { Id = "s1", JobId = "job-1", StartTime = At(Now.AddDays(-1)), EndTime = At(Now.AddDays(-1).AddMinutes(1)), Files = 1, Complete = false, IncompleteReason = "canceled" },
        ];
        Agent.Entries[("s2", "")] =
        [
            new SnapshotEntry { Name = "top.txt", Path = "top.txt", Kind = EntryKind.File, Size = 3, ModifiedAt = Today(8, 0) },
            new SnapshotEntry { Name = "docs", Path = "docs", Kind = EntryKind.Directory, ModifiedAt = Today(8, 0) },
        ];
        Agent.Entries[("s2", "docs")] =
        [
            new SnapshotEntry { Name = "b.txt", Path = "docs/b.txt", Kind = EntryKind.File, Size = 5 },
            new SnapshotEntry { Name = "a.txt", Path = "docs/a.txt", Kind = EntryKind.File, Size = 5 },
        ];
    }

    [Fact]
    public async Task Lists_complete_restore_points_and_hides_partial_ones_unless_asked()
    {
        await ConnectAsync();

        var cut = Render<Restore>(p => p.Add(r => r.JobId, "job-1"));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".snapshot")));
        Assert.Contains("today at 09:31", cut.Find(".snapshot").TextContent, StringComparison.Ordinal);

        cut.Find("#show-incomplete").Change(true);

        Assert.Equal(2, cut.FindAll(".snapshot").Count);
        Assert.Contains("Incomplete", cut.FindAll(".snapshot")[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browses_folders_inside_a_restore_point()
    {
        await ConnectAsync();
        var cut = Render<Restore>(p => p.Add(r => r.JobId, "job-1"));
        cut.WaitForElement(".snapshot button.choose");

        await cut.Find(".snapshot button.choose").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(["docs", "top.txt"], cut.FindAll(".entry .name").Select(e => e.TextContent.Trim())));
        await cut.Find(".entry button.open").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal(["a.txt", "b.txt"], cut.FindAll(".entry .name").Select(e => e.TextContent.Trim())));
        Assert.True(Agent.Called("BrowseSnapshot:s2/docs"));
        Assert.Contains("docs", cut.Find(".crumbs").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restores_the_open_folder_into_a_new_folder_and_reports_the_result()
    {
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();
        await cut.Find(".entry button.open").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".entry").Count));

        Assert.Equal(DefaultTarget, cut.Find("#restore-target").GetAttribute("value"));
        Assert.Contains("never overwrites", cut.Find("#restore-note").TextContent, StringComparison.Ordinal);
        await cut.Find("#start-restore").ClickAsync(new());

        var request = Assert.Single(Agent.Restores);
        Assert.Equal("job-1", request.JobId);
        Assert.Equal("s2", request.SnapshotId);
        Assert.Equal("docs", request.Path);
        Assert.Equal(DefaultTarget, request.TargetFolder);

        cut.WaitForAssertion(() => Assert.True(Agent.Called("WatchRun:restore-new")));
        Agent.Finish("restore-new", new Run
        {
            Id = "restore-new", JobId = "job-1", Kind = RunKind.Restore, State = RunState.Succeeded, Files = 2, Bytes = 10,
            RestoreTarget = DefaultTarget, SnapshotId = "s2", StartedAt = Today(12, 0), FinishedAt = Today(12, 0),
        });

        cut.WaitForAssertion(() => Assert.Contains("Restored 2 files", cut.Find("#restore-result").TextContent, StringComparison.Ordinal));
        await cut.Find("#open-folder").ClickAsync(new());
        Assert.Equal(DefaultTarget, Assert.Single(Shell.Opened));
    }

    [Fact]
    public async Task Restores_just_the_picked_file()
    {
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();

        cut.FindAll(".entry input.pick")[1].Change(true);
        cut.Find("#restore-target").Change(@"D:\Restored\top");
        await cut.Find("#start-restore").ClickAsync(new());

        var request = Assert.Single(Agent.Restores);
        Assert.Equal("top.txt", request.Path);
        Assert.Equal(@"D:\Restored\top", request.TargetFolder);
    }

    [Fact]
    public async Task Restores_everything_from_the_top()
    {
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();

        await cut.Find("#start-restore").ClickAsync(new());

        Assert.Equal("", Assert.Single(Agent.Restores).Path);
    }

    [Fact]
    public async Task A_target_that_is_not_empty_is_refused_with_a_clear_message()
    {
        Agent.Failures["StartRestore"] = Error(AgentErrorKinds.RestoreTargetNotEmpty, "The restore target is not empty.");
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();

        await cut.Find("#start-restore").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("never overwrites", cut.Find(".error-box").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#restore-result"));
    }

    [Fact]
    public async Task A_relative_target_is_refused_before_asking_the_agent()
    {
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();

        cut.Find("#restore-target").Change("Restored");
        await cut.Find("#start-restore").ClickAsync(new());

        Assert.Contains("full path", cut.Find(".error-box").TextContent, StringComparison.Ordinal);
        Assert.Empty(Agent.Restores);
    }

    [Fact]
    public async Task A_restore_that_skipped_items_says_so()
    {
        await ConnectAsync();
        var cut = await OpenSnapshotAsync();
        await cut.Find("#start-restore").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.True(Agent.Called("WatchRun:restore-new")));

        var finished = new Run
        {
            Id = "restore-new", JobId = "job-1", Kind = RunKind.Restore, State = RunState.CompletedWithWarnings, Files = 2, FailedItemCount = 1,
            RestoreTarget = DefaultTarget, SnapshotId = "s2",
        };
        finished.FailedItems.Add(new FailedItem { Path = "docs/a.txt", Error = "access denied" });
        Agent.Finish("restore-new", finished);

        cut.WaitForAssertion(() => Assert.Contains("Completed with warnings", cut.Find("#restore-result").TextContent, StringComparison.Ordinal));
        Assert.Contains("docs/a.txt", cut.Find("#restore-result .failed-items").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Says_when_windows_will_ask_for_approval()
    {
        Agent.Status.CallerCanManage = false;
        await ConnectAsync();

        var cut = await OpenSnapshotAsync();

        Assert.NotEmpty(cut.FindAll(".elevation-note"));
    }

    private async Task<IRenderedComponent<Restore>> OpenSnapshotAsync()
    {
        var cut = Render<Restore>(p => p.Add(r => r.JobId, "job-1"));
        cut.WaitForElement(".snapshot button.choose");
        await cut.Find(".snapshot button.choose").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".entry").Count));
        return cut;
    }
}
