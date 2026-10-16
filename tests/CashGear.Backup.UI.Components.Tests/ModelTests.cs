using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Model;
using CashGear.Backup.UI.Components.Services;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using static CashGear.Backup.UI.Components.Tests.Infrastructure.Sample;

namespace CashGear.Backup.UI.Components.Tests;

public class JobDraftTests
{
    [Fact]
    public void A_new_draft_uses_the_agents_default_retention_and_the_first_destination()
    {
        var draft = JobDraft.ForNew([Destination("dest-7")]);

        Assert.Equal(DestinationChoice.Existing, draft.DestinationChoice);
        Assert.Equal("dest-7", draft.DestinationId);
        var retention = draft.ToSpec("dest-7").Retention;
        Assert.Equal((10, 0, 7, 4, 24, 3), (retention.KeepLatest, retention.KeepHourly, retention.KeepDaily, retention.KeepWeekly, retention.KeepMonthly, retention.KeepAnnual));
        Assert.Equal(DestinationChoice.CreateNew, JobDraft.ForNew([]).DestinationChoice);
    }

    [Fact]
    public void Every_day_is_sent_as_no_days_and_weekdays_in_order()
    {
        var draft = new JobDraft { ScheduleChoice = ScheduleChoice.Daily, DailyTime = "7:05" };
        Assert.Empty(draft.ToSchedule().Daily.DaysOfWeek);
        Assert.Equal(7 * 60 + 5, draft.ToSchedule().Daily.MinuteOfDay);

        draft.DailyDays.Remove(DayOfWeek.Sunday);
        draft.DailyDays.Remove(DayOfWeek.Wednesday);
        Assert.Equal([1, 2, 4, 5, 6], draft.ToSchedule().Daily.DaysOfWeek);
    }

    [Fact]
    public void A_job_round_trips_through_the_draft()
    {
        var job = Job(schedule: new Schedule { Daily = new DailySchedule { MinuteOfDay = 90, DaysOfWeek = { 1, 3 } } });
        job.Spec.Excludes.Add("*.tmp");
        job.Spec.Retention.KeepHourly = 24;

        var draft = JobDraft.FromJob(job);

        Assert.True(draft.IsEdit);
        Assert.Equal("01:30", draft.DailyTime);
        Assert.Equal(job.Spec, draft.ToSpec(job.Spec.DestinationId));
        Assert.Equal([WizardStep.What, WizardStep.When, WizardStep.HowLong, WizardStep.Review], draft.Steps);
    }

    [Theory]
    [InlineData("", @"C:\Data", "name")]
    [InlineData("Data", "Data", "full path")]
    [InlineData("Data", "", "full path")]
    public void What_needs_a_name_and_an_absolute_folder(string name, string source, string expected)
    {
        var errors = new JobDraft { Name = name, SourcePath = source }.Validate(WizardStep.What);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_destination_cannot_be_inside_the_backed_up_folder()
    {
        var draft = new JobDraft
        {
            SourcePath = @"C:\Users\pat", DestinationChoice = DestinationChoice.CreateNew, DestinationName = "Inside",
            DestinationLocation = @"C:\Users\pat\Backups", Password = "long enough", PasswordConfirmation = "long enough",
        };
        Assert.Contains(draft.Validate(WizardStep.Where), e => e.Contains("inside the folder", StringComparison.Ordinal));

        draft.DestinationLocation = @"C:\Users\patricia-backups";
        Assert.Empty(draft.Validate(WizardStep.Where));
    }

    [Fact]
    public void Connecting_needs_a_password_but_no_minimum_or_confirmation()
    {
        var draft = new JobDraft { DestinationChoice = DestinationChoice.ConnectExisting, DestinationName = "Old", DestinationLocation = @"F:\Old" };
        Assert.Contains(draft.Validate(WizardStep.Where), e => e.Contains("password", StringComparison.Ordinal));

        draft.Password = "abc";
        Assert.Empty(draft.Validate(WizardStep.Where));
    }

    [Theory]
    [InlineData(ScheduleChoice.Daily, "25:00", 4, "time of day")]
    [InlineData(ScheduleChoice.Interval, "12:00", 0, "1 to 24 hours")]
    [InlineData(ScheduleChoice.Interval, "12:00", 25, "1 to 24 hours")]
    public void Schedules_are_checked(ScheduleChoice choice, string time, int hours, string expected)
    {
        var draft = new JobDraft { ScheduleChoice = choice, DailyTime = time, IntervalHours = hours };
        Assert.Contains(draft.Validate(WizardStep.When), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_daily_schedule_needs_a_day()
    {
        var draft = new JobDraft { ScheduleChoice = ScheduleChoice.Daily };
        draft.DailyDays.Clear();
        Assert.Contains(draft.Validate(WizardStep.When), e => e.Contains("at least one day", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 7, "latest")]
    [InlineData(1, -1, "between 0 and")]
    [InlineData(1, 10_001, "between 0 and")]
    public void Retention_keeps_the_latest_and_stays_in_range(int latest, int daily, string expected)
    {
        var draft = new JobDraft { KeepLatest = latest, KeepDaily = daily };
        Assert.Contains(draft.Validate(WizardStep.HowLong), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Passwords_can_be_forgotten()
    {
        var draft = new JobDraft { Password = "secret secret", PasswordConfirmation = "secret secret" };
        draft.ForgetPasswords();
        Assert.Equal(("", ""), (draft.Password, draft.PasswordConfirmation));
    }
}

public class FormatTests
{
    private readonly FixedTime _time = new(Now);

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(123L * 1024 * 1024 * 1024, "123 GB")]
    public void Bytes(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void Times_are_relative_to_today()
    {
        Assert.Equal("today at 09:31", Format.When(Today(9, 31), _time));
        Assert.Equal("yesterday at 23:59", Format.When(At(Now.AddHours(-12).AddMinutes(-1)), _time));
        Assert.Equal("tomorrow at 02:00", Format.When(At(Now.AddHours(14)), _time));
        Assert.Equal("Monday at 12:00", Format.When(At(Now.AddDays(-3)), _time));
        Assert.Equal("1 Oct 2026 at 12:00", Format.When(At(Now.AddDays(-14)), _time));
        Assert.Equal("", Format.When(null, _time));
    }

    [Fact]
    public void Schedules_read_like_sentences()
    {
        Assert.Equal("Every day at 02:00", Format.Schedule(new Schedule { Daily = new DailySchedule { MinuteOfDay = 120 } }));
        Assert.Equal("Mon, Fri, Sun at 12:30", Format.Schedule(new Schedule { Daily = new DailySchedule { MinuteOfDay = 750, DaysOfWeek = { 0, 1, 5 } } }));
        Assert.Equal("Every hour", Format.Schedule(new Schedule { Interval = new IntervalSchedule { Hours = 1 } }));
        Assert.Equal("Every 6 hours", Format.Schedule(new Schedule { Interval = new IntervalSchedule { Hours = 6 } }));
        Assert.Equal("Only when you click Back up now", Format.Schedule(null));
    }

    [Fact]
    public void Completed_with_warnings_never_reads_as_success()
    {
        Assert.NotEqual(RunStates.Text(RunState.Succeeded), RunStates.Text(RunState.CompletedWithWarnings));
        Assert.NotEqual(RunStates.Tone(RunState.Succeeded), RunStates.Tone(RunState.CompletedWithWarnings));
    }
}

public class BackupOverviewTests
{
    [Fact]
    public void Agent_problems_come_first()
    {
        Assert.Equal(OverviewLevel.Problem, BackupOverview.Summarize(AgentConnection.NotRunning, null).Level);
        Assert.Equal(OverviewLevel.Problem, BackupOverview.Summarize(AgentConnection.NotTrusted, [Job()]).Level);
        Assert.Equal(OverviewLevel.Info, BackupOverview.Summarize(AgentConnection.Starting, [Job()]).Level);
    }

    [Fact]
    public void Failures_outrank_warnings_which_outrank_success()
    {
        var ok = Job("a", "Photos", BackupRun("1", RunState.Succeeded, Today(9, 0), "a"));
        var warned = Job("b", "Mail", BackupRun("2", RunState.CompletedWithWarnings, Today(9, 0), "b"));
        var failed = Job("c", "Work", BackupRun("3", RunState.Failed, Today(9, 0), "c"));

        Assert.Equal(new BackupOverview(OverviewLevel.Ok, "All backups are up to date"), BackupOverview.Summarize(AgentConnection.Ready, [ok]));
        Assert.Equal(OverviewLevel.Warning, BackupOverview.Summarize(AgentConnection.Ready, [ok, warned]).Level);
        Assert.Equal("Work: the last backup failed", BackupOverview.Summarize(AgentConnection.Ready, [ok, warned, failed]).Text);
        Assert.Equal(OverviewLevel.Warning, BackupOverview.Summarize(AgentConnection.Ready, []).Level);
    }
}

public class OverviewWithAlertsTests
{
    [Fact]
    public void Alerts_decide_the_overview_once_the_agent_is_ready()
    {
        var ok = Job("a", "Photos", BackupRun("1", RunState.Succeeded, Today(9, 0), "a"));
        Alert[] alerts =
        [
            new() { Kind = AlertKind.NoRecentBackup, JobName = "Photos", Message = "The last backup was 5 days ago." },
            new() { Kind = AlertKind.VerifyFailed, JobName = "Photos", Message = "The check found 2 problem(s)." },
        ];

        var overview = BackupOverview.Summarize(AgentConnection.Ready, [ok], alerts);

        Assert.Equal(OverviewLevel.Problem, overview.Level);
        Assert.Equal("Photos: The check found 2 problem(s).", overview.Text);
        Assert.Equal(OverviewLevel.Warning, BackupOverview.Summarize(AgentConnection.Ready, [ok], [alerts[0]]).Level);
        Assert.Equal(OverviewLevel.Problem, BackupOverview.Summarize(AgentConnection.NotRunning, [ok], alerts).Level);
        Assert.Equal("The backup service is not running", BackupOverview.Summarize(AgentConnection.NotRunning, [ok], alerts).Text);
    }
}

public class AgentMonitorTests
{
    [Fact]
    public async Task Tracks_the_agent_and_raises_changed_only_on_change()
    {
        var agent = new FakeAgentClient();
        var monitor = new AgentMonitor(agent);
        var changes = 0;
        monitor.Changed += () => changes++;
        var ct = Xunit.TestContext.Current.CancellationToken;

        await monitor.RefreshAsync(ct);
        Assert.Equal(AgentConnection.Ready, monitor.Connection);
        Assert.True(monitor.CanManage);
        await monitor.RefreshAsync(ct);
        Assert.Equal(1, changes);

        agent.Failures["GetStatus"] = new AgentCallException(Grpc.Core.StatusCode.Unavailable, AgentErrorKinds.AgentNotRunning, "gone");
        await monitor.RefreshAsync(ct);
        Assert.Equal(AgentConnection.NotRunning, monitor.Connection);
        Assert.False(monitor.CanManage);
        Assert.Equal("gone", monitor.Problem);
        Assert.Equal(2, changes);

        agent.Failures["GetStatus"] = new AgentCallException(Grpc.Core.StatusCode.Unavailable, AgentErrorKinds.AgentNotTrusted, "impostor");
        await monitor.RefreshAsync(ct);
        Assert.Equal(AgentConnection.NotTrusted, monitor.Connection);

        agent.Failures.Clear();
        agent.Status.Ready = false;
        await monitor.RefreshAsync(ct);
        Assert.Equal(AgentConnection.Starting, monitor.Connection);
        Assert.Equal(4, changes);
    }
}
