using CashGear.Backup.Agent.Scheduling;

namespace CashGear.Backup.Agent.Tests;

public class ScheduleTests
{
    // Fixed zone so the tests do not depend on the machine's time zone or DST.
    private static readonly TimeZoneInfo s_zone = TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3");

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Daily_runs_at_its_time_every_day()
    {
        var schedule = ScheduleSpec.Daily(2 * 60 + 30);
        Assert.Equal(Local(15, 2, 30), ScheduleMath.NextAfter(schedule, Local(15, 1), s_zone));
        Assert.Equal(Local(16, 2, 30), ScheduleMath.NextAfter(schedule, Local(15, 2, 30), s_zone));
    }

    [Fact]
    public void Daily_on_chosen_days_skips_the_others()
    {
        // 2026-10-15 is a Thursday.
        var schedule = ScheduleSpec.Daily(9 * 60, [DayOfWeek.Monday, DayOfWeek.Saturday]);
        Assert.Equal(Local(17, 9), ScheduleMath.NextAfter(schedule, Local(15, 12), s_zone));
        Assert.Equal(Local(19, 9), ScheduleMath.NextAfter(schedule, Local(17, 9), s_zone));
    }

    [Fact]
    public void Interval_runs_every_n_hours_after_the_last_run()
    {
        Assert.Equal(Local(15, 18), ScheduleMath.NextAfter(ScheduleSpec.EveryHours(6), Local(15, 12), s_zone));
    }

    [Fact]
    public void Manual_never_runs_by_itself()
    {
        Assert.Null(ScheduleMath.NextAfter(ScheduleSpec.Manual, Local(15, 12), s_zone));
        Assert.False(ScheduleMath.IsDue(ScheduleSpec.Manual, Local(1, 0), Local(15, 12), s_zone));
    }

    [Fact]
    public void A_schedule_is_due_when_a_run_time_passed_since_the_last_run()
    {
        var nightly = ScheduleSpec.Daily(2 * 60);
        Assert.True(ScheduleMath.IsDue(nightly, since: Local(14, 2, 1), now: Local(15, 9), s_zone), "missed 02:00 today");
        Assert.False(ScheduleMath.IsDue(nightly, since: Local(15, 2, 1), now: Local(15, 9), s_zone), "already ran after 02:00 today");
        Assert.True(ScheduleMath.IsDue(ScheduleSpec.EveryHours(1), since: Local(15, 6), now: Local(15, 9), s_zone));
        Assert.False(ScheduleMath.IsDue(ScheduleSpec.EveryHours(4), since: Local(15, 6), now: Local(15, 9), s_zone));
    }

    [Fact]
    public void Daily_maps_to_a_quartz_cron_expression()
    {
        Assert.Equal("0 30 2 ? * *", ScheduleMath.Cron(ScheduleSpec.Daily(150)));
        Assert.Equal("0 0 9 ? * MON,SAT", ScheduleMath.Cron(ScheduleSpec.Daily(540, [DayOfWeek.Saturday, DayOfWeek.Monday])));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1440, 0)]
    [InlineData(0, 0b1000_0000)]
    public void Invalid_daily_schedules_are_rejected(int minute, int dayBits) =>
        Assert.NotEmpty(new ScheduleSpec(Data.ScheduleKind.Daily, minute, dayBits, 0).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void Invalid_intervals_are_rejected(int hours) =>
        Assert.NotEmpty(ScheduleSpec.EveryHours(hours).Validate());
}
