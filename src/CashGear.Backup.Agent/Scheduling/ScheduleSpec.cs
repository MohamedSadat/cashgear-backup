using CashGear.Backup.Data;
using Quartz;

namespace CashGear.Backup.Agent.Scheduling;

/// <summary>When a job runs by itself. Times are local to the machine.</summary>
internal sealed record ScheduleSpec(ScheduleKind Kind, int MinuteOfDay, int Days, int IntervalHours)
{
    private const int AllDayBits = 0b111_1111;

    public static ScheduleSpec Manual { get; } = new(ScheduleKind.Manual, 0, 0, 0);

    /// <summary>At <paramref name="minuteOfDay"/> on <paramref name="days"/> (none: every day).</summary>
    public static ScheduleSpec Daily(int minuteOfDay, IEnumerable<DayOfWeek>? days = null) =>
        new(ScheduleKind.Daily, minuteOfDay, days?.Aggregate(0, (bits, day) => bits | (1 << (int)day)) ?? 0, 0);

    public static ScheduleSpec EveryHours(int hours) => new(ScheduleKind.Interval, 0, 0, hours);

    public static ScheduleSpec From(JobRecord job) => job.ScheduleKind switch
    {
        ScheduleKind.Daily => new(ScheduleKind.Daily, job.ScheduleMinuteOfDay, job.ScheduleDays, 0),
        ScheduleKind.Interval => EveryHours(job.ScheduleIntervalHours),
        _ => Manual,
    };

    public IReadOnlyList<DayOfWeek> DaysOfWeek =>
        [.. Enumerable.Range(0, 7).Where(d => (Days & (1 << d)) != 0).Select(d => (DayOfWeek)d)];

    public void CopyTo(JobRecord job)
    {
        job.ScheduleKind = Kind;
        job.ScheduleMinuteOfDay = MinuteOfDay;
        job.ScheduleDays = Days;
        job.ScheduleIntervalHours = IntervalHours;
    }

    public IReadOnlyList<string> Validate() => Kind switch
    {
        ScheduleKind.Manual => [],
        ScheduleKind.Daily when MinuteOfDay is < 0 or >= 24 * 60 => ["A daily schedule needs a time between 00:00 and 23:59."],
        ScheduleKind.Daily when (Days & ~AllDayBits) != 0 => ["Days of the week must be 0 (Sunday) to 6 (Saturday)."],
        ScheduleKind.Daily => [],
        ScheduleKind.Interval when IntervalHours is < 1 or > 24 => ["An interval must be 1 to 24 hours."],
        ScheduleKind.Interval => [],
        _ => [$"Unknown schedule kind {Kind}."],
    };
}

/// <summary>Schedule arithmetic, free of Quartz state so it can be tested and used for catch-up decisions.</summary>
internal static class ScheduleMath
{
    /// <summary>The Quartz cron expression of a daily schedule; null for other kinds.</summary>
    public static string? Cron(ScheduleSpec schedule)
    {
        if (schedule.Kind != ScheduleKind.Daily)
        {
            return null;
        }

        var days = schedule.Days == 0
            ? "*"
            : string.Join(',', schedule.DaysOfWeek.Select(d => d.ToString()[..3].ToUpperInvariant()));
        return $"0 {schedule.MinuteOfDay % 60} {schedule.MinuteOfDay / 60} ? * {days}";
    }

    /// <summary>The first time the schedule fires strictly after <paramref name="after"/>; null if it never does by itself.</summary>
    public static DateTimeOffset? NextAfter(ScheduleSpec schedule, DateTimeOffset after, TimeZoneInfo zone) => schedule.Kind switch
    {
        ScheduleKind.Daily => new CronExpression(Cron(schedule)!) { TimeZone = zone }.GetNextValidTimeAfter(after),
        ScheduleKind.Interval => after + TimeSpan.FromHours(schedule.IntervalHours),
        _ => null,
    };

    /// <summary>Whether a run time passed between <paramref name="since"/> (the last run, or the last schedule change) and now.</summary>
    public static bool IsDue(ScheduleSpec schedule, DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone) =>
        NextAfter(schedule, since, zone) is { } next && next <= now;
}
