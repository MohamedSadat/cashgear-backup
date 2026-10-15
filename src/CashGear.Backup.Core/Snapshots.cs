namespace CashGear.Backup.Core;

/// <summary>One restore point of a job's source folder.</summary>
public sealed record SnapshotInfo
{
    public required string Id { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string SourcePath { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>Engine reference to the snapshot's root folder. Opaque: only pass it back to the engine.</summary>
    public required string RootId { get; init; }

    public long Files { get; init; }
    public long Directories { get; init; }
    public long Bytes { get; init; }

    /// <summary>Items that could not be read when the snapshot was taken.</summary>
    public long FailedItemCount { get; init; }

    /// <summary>Why the snapshot is partial (for example "canceled"); null for a complete snapshot.</summary>
    public string? IncompleteReason { get; init; }

    public bool IsComplete => IncompleteReason is null;
}

public enum SnapshotEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>A file or folder inside a snapshot. <see cref="Path"/> is relative to the snapshot root and uses '/'.</summary>
public sealed record SnapshotEntry(string Name, string Path, SnapshotEntryKind Kind, long Size, DateTimeOffset? ModifiedAt);

/// <summary>Restores a snapshot, or one folder or file in it, into a new folder.</summary>
/// <remarks>
/// There is deliberately no overwrite option: <see cref="TargetFolder"/> must not exist or must be
/// empty, and the engine refuses anything else.
/// </remarks>
public sealed record RestoreRequest
{
    public required SnapshotInfo Snapshot { get; init; }

    /// <summary>
    /// Folder or file inside the snapshot, relative to its root with '/' separators. Empty restores the
    /// whole snapshot. A folder's contents go directly into <see cref="TargetFolder"/>; a file is written
    /// as <c>TargetFolder\name</c>.
    /// </summary>
    public string PathInSnapshot { get; init; } = "";

    /// <summary>Absolute path of a folder that does not exist yet or is empty.</summary>
    public required string TargetFolder { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TargetFolder) || !Path.IsPathFullyQualified(TargetFolder))
        {
            errors.Add("TargetFolder must be an absolute folder path.");
        }

        if (SnapshotPath.Split(PathInSnapshot) is null)
        {
            errors.Add("PathInSnapshot must be a relative path inside the snapshot without '.' or '..' segments.");
        }

        return errors;
    }
}

/// <summary>Paths inside a snapshot: relative, '/'-separated, no '.' or '..' segments.</summary>
public static class SnapshotPath
{
    /// <summary>The segments of <paramref name="path"/> ('\' accepted as a separator), or null when it is not a valid snapshot path.</summary>
    public static IReadOnlyList<string>? Split(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return [];
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = normalized.Trim('/').Split('/');
        return segments.Any(s => s.Length == 0 || s == "." || s == "..") ? null : segments;
    }
}

public sealed record VerifyResult(bool Passed, long ItemsChecked, IReadOnlyList<string> Errors);
