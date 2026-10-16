namespace CashGear.Backup.Core;

/// <summary>One folder backed up to one repository with its own retention, exclusions and compression.</summary>
/// <remarks>
/// A job has exactly one source folder because snapshots are recorded per folder; a "job" with several
/// folders in the UI maps to several of these.
/// </remarks>
public sealed record BackupJob
{
    public required Guid Id { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string Name { get; init; }

    /// <summary>Absolute path of the folder to back up.</summary>
    public required string SourcePath { get; init; }

    public RetentionRule Retention { get; init; } = RetentionRule.Default;

    /// <summary>gitignore-style patterns relative to <see cref="SourcePath"/>, such as <c>*.tmp</c> or <c>node_modules/</c>.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    public BackupCompression Compression { get; init; } = BackupCompression.Balanced;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Id == Guid.Empty)
        {
            errors.Add("Id is required.");
        }

        if (RepositoryId == Guid.Empty)
        {
            errors.Add("RepositoryId is required.");
        }

        if (string.IsNullOrWhiteSpace(SourcePath) || !Path.IsPathFullyQualified(SourcePath))
        {
            errors.Add("SourcePath must be an absolute folder path.");
        }

        if (Excludes.Any(e => string.IsNullOrWhiteSpace(e) || e.Contains('\n') || e.Contains('\r')))
        {
            errors.Add("Exclude patterns must be non-empty single lines.");
        }

        if (!Enum.IsDefined(Compression))
        {
            errors.Add($"Unknown compression '{Compression}'.");
        }

        errors.AddRange(Retention.Validate());
        return errors;
    }
}

/// <summary>How many snapshots to keep. A snapshot is kept when any rule selects it.</summary>
public sealed record RetentionRule
{
    public const int MaxCount = 10_000;

    public static RetentionRule Default { get; } = new();

    /// <summary>The newest N snapshots. Must be at least 1, or the snapshot a run just made could be pruned.</summary>
    public int KeepLatest { get; init; } = 10;

    public int KeepHourly { get; init; }
    public int KeepDaily { get; init; } = 7;
    public int KeepWeekly { get; init; } = 4;
    public int KeepMonthly { get; init; } = 24;
    public int KeepAnnual { get; init; } = 3;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (KeepLatest < 1)
        {
            errors.Add("Retention must keep at least the latest snapshot (KeepLatest >= 1).");
        }

        (string Name, int Value)[] counts =
        [
            (nameof(KeepLatest), KeepLatest), (nameof(KeepHourly), KeepHourly), (nameof(KeepDaily), KeepDaily),
            (nameof(KeepWeekly), KeepWeekly), (nameof(KeepMonthly), KeepMonthly), (nameof(KeepAnnual), KeepAnnual),
        ];
        foreach (var (name, value) in counts.Where(c => c.Value is < 0 or > MaxCount))
        {
            errors.Add($"Retention {name} must be between 0 and {MaxCount}.");
        }

        return errors;
    }
}

public enum BackupCompression
{
    /// <summary>Store data as is.</summary>
    Off,

    /// <summary>Fastest compression, for slow CPUs or fast destinations.</summary>
    Fastest,

    /// <summary>The default: good ratio at low CPU cost.</summary>
    Balanced,

    /// <summary>Smaller repository, more CPU.</summary>
    Smallest,
}
