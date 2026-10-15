using Bunit;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Shared;
using CashGear.Backup.UI.Components.Tests.Infrastructure;

namespace CashGear.Backup.UI.Components.Tests;

public class SharedComponentTests : UiTest
{
    [Theory]
    [InlineData(RunState.Succeeded, "Succeeded", "badge-ok")]
    [InlineData(RunState.CompletedWithWarnings, "Completed with warnings", "badge-warn")]
    [InlineData(RunState.Failed, "Failed", "badge-error")]
    [InlineData(RunState.Canceled, "Canceled", "badge-muted")]
    [InlineData(RunState.Running, "Running", "badge-info")]
    public void Run_states_have_distinct_words_and_colours(RunState state, string text, string css)
    {
        var cut = Render<RunStateBadge>(p => p.Add(b => b.State, state));

        var badge = cut.Find(".badge");
        Assert.Equal(text, badge.TextContent.Trim());
        Assert.Contains(css, badge.ClassList);
    }

    [Fact]
    public async Task The_banner_says_when_the_service_is_not_running_and_retries()
    {
        Agent.Failures["GetStatus"] = Error(AgentErrorKinds.AgentNotRunning, "The CashGear Backup service is not running or did not answer.", Grpc.Core.StatusCode.Unavailable);
        await ConnectAsync();
        var cut = Render<AgentBanner>();

        Assert.Contains("is not running", cut.Find(".agent-banner").TextContent, StringComparison.Ordinal);

        Agent.Failures.Clear();
        await cut.Find(".agent-banner button.retry").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".agent-banner")));
    }

    [Fact]
    public async Task The_banner_warns_about_an_impostor_on_the_pipe()
    {
        Agent.Failures["GetStatus"] = Error(AgentErrorKinds.AgentNotTrusted, "The program answering on the backup service's connection (process 1234) is not the CashGear Backup service, so nothing was sent to it.");
        await ConnectAsync();

        var cut = Render<AgentBanner>();

        var text = cut.Find(".agent-banner").TextContent;
        Assert.Contains("not the CashGear Backup service", text, StringComparison.Ordinal);
        Assert.Contains("process 1234", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_banner_says_when_the_service_is_still_starting()
    {
        Agent.Status.Ready = false;
        await ConnectAsync();

        var cut = Render<AgentBanner>();

        Assert.Contains("starting", cut.Find(".agent-banner").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_banner_follows_the_monitor()
    {
        await ConnectAsync();
        var cut = Render<AgentBanner>();
        Assert.Empty(cut.FindAll(".agent-banner"));

        Agent.Failures["GetStatus"] = Error(AgentErrorKinds.AgentNotRunning, "gone", Grpc.Core.StatusCode.Unavailable);
        await Monitor.RefreshAsync(Ct);

        cut.WaitForAssertion(() => Assert.Contains("is not running", cut.Find(".agent-banner").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_elevation_note_shows_only_when_windows_will_ask()
    {
        Agent.Status.CallerCanManage = false;
        await ConnectAsync();
        var cut = Render<ElevationNote>();
        Assert.Contains("administrator approval", cut.Find(".elevation-note").TextContent, StringComparison.Ordinal);

        Agent.Status.CallerCanManage = true;
        await Monitor.RefreshAsync(Ct);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".elevation-note")));
    }

    [Fact]
    public void Failed_items_list_the_first_ones_and_count_the_rest()
    {
        var run = new Run { State = RunState.CompletedWithWarnings, FailedItemCount = 12, Kind = RunKind.Backup };
        run.FailedItems.Add(new FailedItem { Path = "a.pst", Error = "in use" });
        run.FailedItems.Add(new FailedItem { Path = "b.pst", Error = "access denied" });

        var cut = Render<FailedItems>(p => p.Add(f => f.Run, run));

        var text = cut.Find(".failed-items").TextContent;
        Assert.Contains("12 items could not be backed up", text, StringComparison.Ordinal);
        Assert.Contains("a.pst", text, StringComparison.Ordinal);
        Assert.Contains("access denied", text, StringComparison.Ordinal);
        Assert.Contains("and 10 more", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_folder_field_fills_in_the_picked_folder()
    {
        Picker.Answer = @"D:\Photos";
        var value = "";
        var cut = Render<FolderField>(p => p
            .Add(f => f.Id, "source")
            .Add(f => f.Title, "Choose the folder to back up")
            .Add(f => f.Value, value)
            .Add(f => f.ValueChanged, (string v) => value = v));

        await cut.Find("#source-browse").ClickAsync(new());

        Assert.Equal(@"D:\Photos", value);
        Assert.Equal("Choose the folder to back up", Assert.Single(Picker.Titles));
    }
}
