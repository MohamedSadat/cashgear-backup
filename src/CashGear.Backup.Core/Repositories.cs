namespace CashGear.Backup.Core;

/// <summary>
/// Where a repository lives. <see cref="RepositoryId"/> is our stable key for it (from state.db); jobs,
/// snapshots and runs refer to the repository by this id.
/// </summary>
public abstract record DestinationSpec(Guid RepositoryId)
{
    /// <summary>Location for logs and the UI. Never contains credentials.</summary>
    public abstract string DisplayLocation { get; }

    public virtual IReadOnlyList<string> Validate() =>
        RepositoryId == Guid.Empty ? ["RepositoryId is required."] : [];
}

/// <summary>A folder on a local disk, removable drive or UNC share that the agent can already reach.</summary>
public sealed record FileSystemDestination(Guid RepositoryId, string Path) : DestinationSpec(RepositoryId)
{
    public override string DisplayLocation => Path;

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path))
        {
            errors.Add("Path must be an absolute folder path.");
        }

        return errors;
    }
}

/// <summary>A repository the engine has created or connected.</summary>
public sealed record RepositoryInfo
{
    public required Guid RepositoryId { get; init; }

    /// <summary>Display location of the destination.</summary>
    public required string Location { get; init; }

    /// <summary>The <c>user@host</c> identity this machine's snapshots are recorded under.</summary>
    public required string ClientIdentity { get; init; }

    public string? Encryption { get; init; }
    public string? Hashing { get; init; }
    public bool SupportsCompression { get; init; }
}
