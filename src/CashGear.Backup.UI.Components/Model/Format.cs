using System.Globalization;
using CashGear.Backup.Contracts.V1;
using Google.Protobuf.WellKnownTypes;

namespace CashGear.Backup.UI.Components.Model;

/// <summary>Numbers, times and schedules as the UI shows them (English for now).</summary>
public static class Format
{
    private static readonly CultureInfo s_culture = CultureInfo.InvariantCulture;

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString("N0", s_culture)} B" : $"{value.ToString(value < 10 ? "0.0" : "0", s_culture)} {units[unit]}";
    }

    public static string Count(long value, string one, string many) =>
        $"{value.ToString("N0", s_culture)} {(value == 1 ? one : many)}";

    /// <summary>"today at 14:32", "yesterday at 09:05", "Monday at 12:00", "3 Oct 2026 at 12:00"; empty for no time.</summary>
    public static string When(Timestamp? timestamp, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        if (timestamp is null)
        {
            return "";
        }

        var local = TimeZoneInfo.ConvertTime(timestamp.ToDateTimeOffset(), time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        var clock = local.ToString("HH:mm", s_culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => $"today at {clock}",
            -1 => $"yesterday at {clock}",
            1 => $"tomorrow at {clock}",
            > -7 and < 7 => $"{local.ToString("dddd", s_culture)} at {clock}",
            _ => $"{local.ToString("d MMM yyyy", s_culture)} at {clock}",
        };
    }

    /// <summary>The local date and time as a sortable stamp, for folder names: "2026-10-15 1432".</summary>
    public static string Stamp(Timestamp timestamp, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(time);
        return TimeZoneInfo.ConvertTime(timestamp.ToDateTimeOffset(), time.LocalTimeZone).ToString("yyyy-MM-dd HHmm", s_culture);
    }

    public static string Duration(Timestamp? start, Timestamp? end)
    {
        if (start is null || end is null)
        {
            return "";
        }

        var span = end.ToDateTimeOffset() - start.ToDateTimeOffset();
        return span.TotalSeconds < 1 ? "under a second"
            : span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    public static string Schedule(Schedule? schedule)
    {
        switch (schedule?.KindCase)
        {
            case Contracts.V1.Schedule.KindOneofCase.Daily:
                var minute = schedule.Daily.MinuteOfDay;
                var clock = $"{minute / 60:00}:{minute % 60:00}";
                var days = schedule.Daily.DaysOfWeek;
                if (days.Count is 0 or 7)
                {
                    return $"Every day at {clock}";
                }

                var names = JobDraft.AllDays.Where(d => days.Contains((int)d)).Select(d => d.ToString()[..3]);
                return $"{string.Join(", ", names)} at {clock}";
            case Contracts.V1.Schedule.KindOneofCase.Interval:
                return schedule.Interval.Hours == 1 ? "Every hour" : $"Every {schedule.Interval.Hours} hours";
            default:
                return "Only when you click Back up now";
        }
    }

    public static string Compression(Compression compression) => compression switch
    {
        Contracts.V1.Compression.Off => "Off",
        Contracts.V1.Compression.Fastest => "Fastest",
        Contracts.V1.Compression.Smallest => "Smallest",
        _ => "Balanced",
    };

    public static string Trigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Scheduled => "Scheduled",
        RunTrigger.CatchUp => "Catch-up (missed while the PC was off)",
        _ => "Manual",
    };
}

/// <summary>Run states as the user sees them. Completed with warnings is never shown as a success.</summary>
public static class RunStates
{
    public static string Text(RunState state) => state switch
    {
        RunState.Running => "Running",
        RunState.Succeeded => "Succeeded",
        RunState.CompletedWithWarnings => "Completed with warnings",
        RunState.Failed => "Failed",
        RunState.Canceled => "Canceled",
        _ => "Unknown",
    };

    /// <summary>The badge style: ok, warn, error, info or muted.</summary>
    public static string Tone(RunState state) => state switch
    {
        RunState.Running => "info",
        RunState.Succeeded => "ok",
        RunState.CompletedWithWarnings => "warn",
        RunState.Failed => "error",
        _ => "muted",
    };
}
