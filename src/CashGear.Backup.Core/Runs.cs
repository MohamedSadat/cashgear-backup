namespace CashGear.Backup.Core;

public enum RunKind
{
    Backup,
    Restore,

    /// <summary>A check of a job's backups (<see cref="IBackupEngine.VerifyAsync"/>), recorded by the agent.</summary>
    Verify,
}

public enum RunState
{
    Running,

    /// <summary>
    /// Backup: a new, complete snapshot was recorded and nothing failed or was skipped.
    /// Restore: every item was restored.
    /// </summary>
    Succeeded,

    /// <summary>A complete snapshot was recorded but some items could not be read; see <see cref="RunResult.FailedItems"/>.</summary>
    CompletedWithWarnings,

    /// <summary>No usable result. A backup that failed recorded no restore point.</summary>
    Failed,

    /// <summary>Stopped on request. A canceled backup is never a restore point.</summary>
    Canceled,
}

/// <summary>A started backup or restore. Pass it back to watch or cancel the run.</summary>
public sealed record RunHandle(Guid Id, RunKind Kind, Guid RepositoryId, Guid? JobId, DateTimeOffset StartedAt);

/// <summary>A point-in-time view of a run. The last update of every run has <see cref="Result"/> set.</summary>
public sealed record RunProgress
{
    public required RunHandle Run { get; init; }
    public required RunState State { get; init; }

    /// <summary>Backup: files examined so far (changed and unchanged). Restore: files restored.</summary>
    public long FilesProcessed { get; init; }

    /// <summary>Backup: bytes examined so far (changed and unchanged). Restore: bytes restored.</summary>
    public long BytesProcessed { get; init; }

    /// <summary>Backup only: files whose content was read because they are new or changed.</summary>
    public long FilesHashed { get; init; }

    /// <summary>Backup only: bytes read from new or changed files.</summary>
    public long BytesHashed { get; init; }

    /// <summary>Backup only: the engine's estimate of the source size, when it has one.</summary>
    public long? EstimatedFiles { get; init; }

    public long? EstimatedBytes { get; init; }

    public long ErrorCount { get; init; }

    public RunResult? Result { get; init; }
}

public sealed record FailedItem(string Path, string Error);

/// <summary>The outcome of a finished run.</summary>
public sealed record RunResult
{
    public required RunState State { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Backup: the snapshot this run recorded. Set only for Succeeded and CompletedWithWarnings.</summary>
    public string? SnapshotId { get; init; }

    public long Files { get; init; }
    public long Bytes { get; init; }

    /// <summary>Items that could not be backed up or restored. May exceed <see cref="FailedItems"/>, which can be truncated.</summary>
    public long FailedItemCount { get; init; }

    public IReadOnlyList<FailedItem> FailedItems { get; init; } = [];

    /// <summary>Why the run failed or was canceled; local paths may appear, secrets never do.</summary>
    public string? Message { get; init; }
}
