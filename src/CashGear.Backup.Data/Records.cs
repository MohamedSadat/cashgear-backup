using CashGear.Backup.Core;

namespace CashGear.Backup.Data;

public enum DestinationKind
{
    FileSystem = 1,

    /// <summary>A folder on an SMB share (\\server\share\folder) reached with its own account.</summary>
    NetworkShare = 2,

    Sftp = 3,

    /// <summary>Amazon S3 or an S3-compatible service.</summary>
    S3 = 4,
}

/// <summary>A repository the agent backs up to.</summary>
public sealed class DestinationRecord
{
    /// <summary>Also the repository id the engine knows it by.</summary>
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public DestinationKind Kind { get; set; }
    public required string Location { get; set; }

    /// <summary>The repository password, DPAPI-protected. The plaintext is never stored.</summary>
    public required byte[] ProtectedPassword { get; set; }

    /// <summary>
    /// Connection settings that are not secret, as JSON: the share account, the SFTP server and its host keys, the
    /// S3 endpoint, bucket and access key ID. Null for a plain folder.
    /// </summary>
    public string? Settings { get; set; }

    /// <summary>The destination's own credential (share or SFTP password, SFTP private key, S3 secret key), DPAPI-protected.</summary>
    public byte[]? ProtectedCredential { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum ScheduleKind
{
    Manual = 0,

    /// <summary>At <see cref="JobRecord.ScheduleMinuteOfDay"/> on the days in <see cref="JobRecord.ScheduleDays"/>.</summary>
    Daily = 1,

    /// <summary>Every <see cref="JobRecord.ScheduleIntervalHours"/> hours.</summary>
    Interval = 2,
}

/// <summary>A backup job: one source folder to one destination, with its retention and schedule.</summary>
public sealed class JobRecord
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid DestinationId { get; set; }
    public required string SourcePath { get; set; }

    public int KeepLatest { get; set; }
    public int KeepHourly { get; set; }
    public int KeepDaily { get; set; }
    public int KeepWeekly { get; set; }
    public int KeepMonthly { get; set; }
    public int KeepAnnual { get; set; }

    public List<string> Excludes { get; set; } = [];
    public BackupCompression Compression { get; set; }

    public ScheduleKind ScheduleKind { get; set; }
    public int ScheduleMinuteOfDay { get; set; }

    /// <summary>Bit per <see cref="DayOfWeek"/> (Sunday = 1); 0 means every day.</summary>
    public int ScheduleDays { get; set; }

    public int ScheduleIntervalHours { get; set; }

    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum RunTrigger
{
    Manual = 1,
    Scheduled = 2,

    /// <summary>A scheduled time passed while the agent was not running (PC off or asleep).</summary>
    CatchUp = 3,
}

/// <summary>One backup or restore, kept as history after the job that ran it is deleted.</summary>
public sealed class RunRecord
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public RunKind Kind { get; set; }
    public RunState State { get; set; }
    public RunTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Backup: the snapshot it recorded. Restore: the snapshot it restored from.</summary>
    public string? SnapshotId { get; set; }

    public long Files { get; set; }
    public long Bytes { get; set; }
    public long FailedItemCount { get; set; }

    /// <summary>The first failed items (see <see cref="StateDb.MaxStoredFailedItems"/>).</summary>
    public List<FailedItemRecord> FailedItems { get; set; } = [];

    public string? Message { get; set; }
    public string? RestoreTarget { get; set; }
}

public sealed class FailedItemRecord
{
    public required string Path { get; set; }
    public required string Error { get; set; }
}

public sealed class SettingRecord
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
