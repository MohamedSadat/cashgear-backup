using System.Globalization;
using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.UI.Components.Model;

public enum WizardStep
{
    What,
    Where,
    When,
    HowLong,
    Review,
}

public enum DestinationChoice
{
    /// <summary>A destination the agent already knows.</summary>
    Existing,

    /// <summary>Create a new, encrypted repository at a folder.</summary>
    CreateNew,

    /// <summary>Connect a repository made earlier (a reinstall, another PC).</summary>
    ConnectExisting,
}

public enum ScheduleChoice
{
    Manual,
    Daily,
    Interval,
}

/// <summary>
/// What the job wizard has collected so far, its checks per step, and the <see cref="JobSpec"/> it becomes.
/// The checks mirror the agent's (the agent checks again); passwords live here only until the destination is
/// created or connected.
/// </summary>
public sealed class JobDraft
{
    /// <summary>The agent's minimum for a new repository's password.</summary>
    public const int MinimumPasswordLength = 8;

    public const int MaxNameLength = 200;
    public const int MaxKeepCount = 10_000;

    public static readonly IReadOnlyList<DayOfWeek> AllDays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Set when editing an existing job: its destination and folder cannot change.</summary>
    public string? JobId { get; init; }

    public bool IsEdit => JobId is not null;

    // What
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";

    /// <summary>One gitignore-style pattern per line.</summary>
    public string ExcludesText { get; set; } = "";

    // Where
    public DestinationChoice DestinationChoice { get; set; } = DestinationChoice.CreateNew;
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public string DestinationLocation { get; set; } = "";
    public string Password { get; set; } = "";
    public string PasswordConfirmation { get; set; } = "";

    // When
    public ScheduleChoice ScheduleChoice { get; set; } = ScheduleChoice.Daily;

    /// <summary>"HH:mm", local time.</summary>
    public string DailyTime { get; set; } = "12:00";

    public HashSet<DayOfWeek> DailyDays { get; } = [.. AllDays];
    public int IntervalHours { get; set; } = 4;

    // How long (the agent's defaults)
    public int KeepLatest { get; set; } = 10;
    public int KeepHourly { get; set; }
    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 24;
    public int KeepAnnual { get; set; } = 3;

    public Compression Compression { get; set; } = Compression.Balanced;
    public bool Enabled { get; set; } = true;

    public IReadOnlyList<WizardStep> Steps => IsEdit
        ? [WizardStep.What, WizardStep.When, WizardStep.HowLong, WizardStep.Review]
        : [WizardStep.What, WizardStep.Where, WizardStep.When, WizardStep.HowLong, WizardStep.Review];

    public IReadOnlyList<string> Excludes =>
        [.. ExcludesText.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    /// <summary>A new job; uses an existing destination when there is one.</summary>
    public static JobDraft ForNew(IReadOnlyList<Destination> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var draft = new JobDraft();
        if (destinations.Count > 0)
        {
            draft.DestinationChoice = DestinationChoice.Existing;
            draft.DestinationId = destinations[0].Id;
        }

        return draft;
    }

    public static JobDraft FromJob(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var spec = job.Spec;
        var draft = new JobDraft
        {
            JobId = job.Id,
            Name = spec.Name,
            SourcePath = spec.SourcePath,
            ExcludesText = string.Join('\n', spec.Excludes),
            DestinationChoice = DestinationChoice.Existing,
            DestinationId = spec.DestinationId,
            Compression = spec.Compression == Compression.Unspecified ? Compression.Balanced : spec.Compression,
            Enabled = spec.Enabled,
        };

        if (spec.Retention is { } r)
        {
            (draft.KeepLatest, draft.KeepHourly, draft.KeepDaily, draft.KeepWeekly, draft.KeepMonthly, draft.KeepAnnual) =
                (r.KeepLatest, r.KeepHourly, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepAnnual);
        }

        switch (spec.Schedule?.KindCase)
        {
            case Schedule.KindOneofCase.Daily:
                draft.ScheduleChoice = ScheduleChoice.Daily;
                var minute = spec.Schedule.Daily.MinuteOfDay;
                draft.DailyTime = $"{minute / 60:00}:{minute % 60:00}";
                if (spec.Schedule.Daily.DaysOfWeek.Count > 0)
                {
                    draft.DailyDays.Clear();
                    draft.DailyDays.UnionWith(spec.Schedule.Daily.DaysOfWeek.Where(d => d is >= 0 and <= 6).Select(d => (DayOfWeek)d));
                }

                break;
            case Schedule.KindOneofCase.Interval:
                draft.ScheduleChoice = ScheduleChoice.Interval;
                draft.IntervalHours = spec.Schedule.Interval.Hours;
                break;
            default:
                draft.ScheduleChoice = ScheduleChoice.Manual;
                break;
        }

        return draft;
    }

    public IReadOnlyList<string> Validate(WizardStep step) => step switch
    {
        WizardStep.What => ValidateWhat(),
        WizardStep.Where => ValidateWhere(),
        WizardStep.When => ValidateWhen(),
        WizardStep.HowLong => ValidateHowLong(),
        _ => [.. Steps.Where(s => s != WizardStep.Review).SelectMany(Validate)],
    };

    /// <summary>The job as the agent takes it, on <paramref name="destinationId"/>.</summary>
    public JobSpec ToSpec(string destinationId)
    {
        var spec = new JobSpec
        {
            Name = Name.Trim(),
            DestinationId = destinationId,
            SourcePath = SourcePath.Trim(),
            Retention = new Retention
            {
                KeepLatest = KeepLatest, KeepHourly = KeepHourly, KeepDaily = KeepDaily,
                KeepWeekly = KeepWeekly, KeepMonthly = KeepMonthly, KeepAnnual = KeepAnnual,
            },
            Compression = Compression,
            Schedule = ToSchedule(),
            Enabled = Enabled,
        };
        spec.Excludes.AddRange(Excludes);
        return spec;
    }

    public Schedule ToSchedule()
    {
        switch (ScheduleChoice)
        {
            case ScheduleChoice.Daily:
                var daily = new DailySchedule { MinuteOfDay = ParseMinuteOfDay(DailyTime) ?? 0 };
                if (DailyDays.Count < 7)
                {
                    daily.DaysOfWeek.AddRange(DailyDays.Select(d => (int)d).Order());
                }

                return new Schedule { Daily = daily };
            case ScheduleChoice.Interval:
                return new Schedule { Interval = new IntervalSchedule { Hours = IntervalHours } };
            default:
                return new Schedule { Manual = new ManualSchedule() };
        }
    }

    /// <summary>Called once the agent has the password (or the wizard is left): it is not kept around.</summary>
    public void ForgetPasswords()
    {
        Password = "";
        PasswordConfirmation = "";
    }

    public static int? ParseMinuteOfDay(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? (time.Hour * 60) + time.Minute
            : null;

    private List<string> ValidateWhat()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MaxNameLength)
        {
            errors.Add($"Give the backup a name (at most {MaxNameLength} characters).");
        }

        if (!IsAbsolute(SourcePath))
        {
            errors.Add("Choose the folder to back up (a full path such as C:\\Users\\you\\Documents).");
        }

        return errors;
    }

    private List<string> ValidateWhere()
    {
        var errors = new List<string>();
        switch (DestinationChoice)
        {
            case DestinationChoice.Existing:
                if (string.IsNullOrEmpty(DestinationId))
                {
                    errors.Add("Choose where to back up to.");
                }

                break;
            case DestinationChoice.CreateNew:
            case DestinationChoice.ConnectExisting:
                if (string.IsNullOrWhiteSpace(DestinationName) || DestinationName.Trim().Length > MaxNameLength)
                {
                    errors.Add($"Give the destination a name (at most {MaxNameLength} characters).");
                }

                if (!IsAbsolute(DestinationLocation))
                {
                    errors.Add("Choose the destination folder (a full path such as E:\\Backups or \\\\nas\\backups).");
                }
                else if (IsAbsolute(SourcePath) && IsSameOrInside(DestinationLocation, SourcePath))
                {
                    errors.Add("The destination cannot be inside the folder you back up.");
                }

                if (DestinationChoice == DestinationChoice.CreateNew)
                {
                    if (Password.Length < MinimumPasswordLength)
                    {
                        errors.Add($"The password must be at least {MinimumPasswordLength} characters.");
                    }
                    else if (Password != PasswordConfirmation)
                    {
                        errors.Add("The two passwords do not match.");
                    }
                }
                else if (Password.Length == 0)
                {
                    errors.Add("Enter the password of the existing backup.");
                }

                break;
        }

        return errors;
    }

    private List<string> ValidateWhen()
    {
        var errors = new List<string>();
        switch (ScheduleChoice)
        {
            case ScheduleChoice.Daily:
                if (ParseMinuteOfDay(DailyTime) is null)
                {
                    errors.Add("Enter the time of day as hours and minutes, for example 12:30.");
                }

                if (DailyDays.Count == 0)
                {
                    errors.Add("Choose at least one day.");
                }

                break;
            case ScheduleChoice.Interval when IntervalHours is < 1 or > 24:
                errors.Add("Back up every 1 to 24 hours.");
                break;
        }

        return errors;
    }

    private List<string> ValidateHowLong()
    {
        var errors = new List<string>();
        if (KeepLatest < 1)
        {
            errors.Add("Keep at least the latest backup.");
        }

        if (new[] { KeepLatest, KeepHourly, KeepDaily, KeepWeekly, KeepMonthly, KeepAnnual }.Any(n => n is < 0 or > MaxKeepCount))
        {
            errors.Add($"Each number of backups to keep must be between 0 and {MaxKeepCount}.");
        }

        return errors;
    }

    private static bool IsAbsolute(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path.Trim());

    private static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim())) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
