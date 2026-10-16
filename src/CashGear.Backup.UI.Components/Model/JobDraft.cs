using System.Globalization;
using CashGear.Backup.Client.Security;
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

/// <summary>What kind of place a new or earlier destination is.</summary>
public enum DestinationType
{
    /// <summary>A folder on this PC, another disk or a USB drive.</summary>
    Folder,

    /// <summary>An SMB share opened with its own account.</summary>
    NetworkShare,

    Sftp,

    /// <summary>Amazon S3 or an S3-compatible service.</summary>
    S3,
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

    public DestinationType DestinationType { get; set; } = DestinationType.Folder;

    // Network share
    public string ShareUsername { get; set; } = "";
    public string SharePassword { get; set; } = "";

    // SFTP. Changing the server forgets the host keys read from the old one.
    private string _sftpHost = "";
    private int _sftpPort = 22;

    public string SftpHost
    {
        get => _sftpHost;
        set
        {
            if (value != _sftpHost)
            {
                _sftpHost = value;
                ForgetHostKeys();
            }
        }
    }

    public int SftpPort
    {
        get => _sftpPort;
        set
        {
            if (value != _sftpPort)
            {
                _sftpPort = value;
                ForgetHostKeys();
            }
        }
    }

    public string SftpUsername { get; set; } = "";
    public bool SftpUseKey { get; set; }
    public string SftpPassword { get; set; } = "";
    public string SftpPrivateKey { get; set; } = "";
    public string SftpPath { get; set; } = "";

    /// <summary>The keys the server showed when it was checked; empty until then.</summary>
    public IReadOnlyList<SftpHostKey> SftpHostKeys { get; private set; } = [];

    /// <summary>The user said the fingerprints match the server.</summary>
    public bool SftpHostKeysConfirmed { get; set; }

    // S3
    public string S3Endpoint { get; set; } = "s3.amazonaws.com";
    public string S3Region { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string S3Prefix { get; set; } = "";
    public string S3AccessKeyId { get; set; } = "";
    public string S3SecretKey { get; set; } = "";
    public bool S3UseHttps { get; set; } = true;

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
        var draft = new JobDraft { S3Prefix = Environment.MachineName.ToLowerInvariant() + "/" };
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

    /// <summary>Called once the agent has the passwords (or the wizard is left): they are not kept around.</summary>
    public void ForgetPasswords()
    {
        Password = "";
        PasswordConfirmation = "";
        SharePassword = "";
        SftpPassword = "";
        SftpPrivateKey = "";
        S3SecretKey = "";
    }

    public void SetHostKeys(IReadOnlyList<SftpHostKey> keys)
    {
        SftpHostKeys = keys;
        SftpHostKeysConfirmed = false;
    }

    private void ForgetHostKeys()
    {
        SftpHostKeys = [];
        SftpHostKeysConfirmed = false;
    }

    public DestinationKind DestinationKind => DestinationType switch
    {
        DestinationType.NetworkShare => DestinationKind.NetworkShare,
        DestinationType.Sftp => DestinationKind.Sftp,
        DestinationType.S3 => DestinationKind.S3,
        _ => DestinationKind.FileSystem,
    };

    /// <summary>Where the new destination is, for the review page.</summary>
    public string DestinationDisplay => DestinationType switch
    {
        DestinationType.Sftp => $"sftp://{SftpUsername.Trim()}@{SftpHost.Trim()}:{SftpPort}{SftpPath.Trim()}",
        DestinationType.S3 => $"s3://{S3Bucket.Trim()}/{S3Prefix.Trim()} at {S3Endpoint.Trim()}",
        _ => DestinationLocation.Trim(),
    };

    public CreateDestinationRequest ToCreateDestination()
    {
        var request = new CreateDestinationRequest { Name = DestinationName.Trim(), Kind = DestinationKind, Location = DestinationLocation.Trim(), Password = Password };
        switch (DestinationType)
        {
            case DestinationType.NetworkShare:
                request.Share = Share();
                break;
            case DestinationType.Sftp:
                request.Sftp = Sftp();
                break;
            case DestinationType.S3:
                request.S3 = S3();
                break;
        }

        return request;
    }

    public ConnectDestinationRequest ToConnectDestination()
    {
        var request = new ConnectDestinationRequest { Name = DestinationName.Trim(), Kind = DestinationKind, Location = DestinationLocation.Trim(), Password = Password };
        switch (DestinationType)
        {
            case DestinationType.NetworkShare:
                request.Share = Share();
                break;
            case DestinationType.Sftp:
                request.Sftp = Sftp();
                break;
            case DestinationType.S3:
                request.S3 = S3();
                break;
        }

        return request;
    }

    private ShareSettings Share() => new() { Username = ShareUsername.Trim(), Password = SharePassword };

    private SftpSettings Sftp()
    {
        var settings = new SftpSettings
        {
            Host = SftpHost.Trim(),
            Port = SftpPort,
            Username = SftpUsername.Trim(),
            Password = SftpUseKey ? "" : SftpPassword,
            PrivateKey = SftpUseKey ? SftpPrivateKey.Trim() + "\n" : "",
            Path = SftpPath.Trim(),
        };
        settings.KnownHosts.AddRange(SftpHostKeys.Select(k => k.KnownHostsLine(SftpHost.Trim(), SftpPort)));
        return settings;
    }

    private S3Settings S3() => new()
    {
        Endpoint = S3Endpoint.Trim(),
        Region = S3Region.Trim(),
        Bucket = S3Bucket.Trim(),
        Prefix = S3Prefix.Trim(),
        AccessKeyId = S3AccessKeyId.Trim(),
        SecretAccessKey = S3SecretKey,
        InsecureHttp = !S3UseHttps,
    };

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

                errors.AddRange(ValidatePlace());

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

    /// <summary>The kind-specific settings of a new or earlier destination.</summary>
    private List<string> ValidatePlace()
    {
        var errors = new List<string>();
        switch (DestinationType)
        {
            case DestinationType.Folder:
            case DestinationType.NetworkShare:
                var location = DestinationLocation.Trim();
                if (DestinationType == DestinationType.NetworkShare && !IsShare(location))
                {
                    errors.Add("Enter the share as \\\\server\\share\\folder.");
                }
                else if (!IsAbsolute(location))
                {
                    errors.Add("Choose the destination folder (a full path such as E:\\Backups or \\\\nas\\backups).");
                }
                else if (IsAbsolute(SourcePath) && IsSameOrInside(location, SourcePath))
                {
                    errors.Add("The destination cannot be inside the folder you back up.");
                }

                if (DestinationType == DestinationType.NetworkShare && (ShareUsername.Trim().Length == 0 || SharePassword.Length == 0))
                {
                    errors.Add("Enter the account (and its password) the backup service should use for the share.");
                }

                break;
            case DestinationType.Sftp:
                if (SftpHost.Trim().Length == 0 || SftpUsername.Trim().Length == 0 || SftpPath.Trim().Length == 0)
                {
                    errors.Add("Enter the SFTP server, the username and the folder on the server.");
                }

                if (SftpPort is < 1 or > 65535)
                {
                    errors.Add("The SFTP port must be 1 to 65535.");
                }

                if (SftpUseKey ? SftpPrivateKey.Trim().Length == 0 : SftpPassword.Length == 0)
                {
                    errors.Add(SftpUseKey ? "Paste the private key." : "Enter the SFTP password.");
                }

                if (SftpHostKeys.Count == 0)
                {
                    errors.Add("Check the server first: CashGear shows its fingerprints so you can make sure it is the right server.");
                }
                else if (!SftpHostKeysConfirmed)
                {
                    errors.Add("Confirm that the fingerprints match the server.");
                }

                break;
            case DestinationType.S3:
                if (S3Endpoint.Trim().Length == 0 || S3Endpoint.Contains("://", StringComparison.Ordinal) || S3Bucket.Trim().Length == 0)
                {
                    errors.Add("Enter the service's endpoint (without https://) and the bucket.");
                }

                if (S3AccessKeyId.Trim().Length == 0 || S3SecretKey.Length == 0)
                {
                    errors.Add("Enter the access key ID and the secret access key.");
                }

                break;
        }

        return errors;
    }

    private static bool IsShare(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

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
