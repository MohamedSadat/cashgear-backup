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

/// <summary>
/// A folder on an SFTP server. Exactly one of <see cref="Password"/> and <see cref="PrivateKey"/>.
/// <see cref="KnownHosts"/> holds a known_hosts line for every host key the server offers, as the user confirmed
/// them: a server that presents another key is refused (a man in the middle, or a reinstalled server).
/// </summary>
public sealed record SftpDestination(Guid RepositoryId, string Host, int Port, string Username, string Path, IReadOnlyList<string> KnownHosts)
    : DestinationSpec(RepositoryId)
{
    public SecureSecret? Password { get; init; }

    /// <summary>An OpenSSH private key (the text of the key file).</summary>
    public SecureSecret? PrivateKey { get; init; }

    public override string DisplayLocation => $"sftp://{Username}@{Host}:{Port}{(Path.StartsWith('/') ? "" : "/")}{Path}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(Host) || Host.Contains(' ', StringComparison.Ordinal))
        {
            errors.Add("Host must be a server name or address.");
        }

        if (Port is < 1 or > 65535)
        {
            errors.Add("Port must be 1 to 65535.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            errors.Add("Username is required.");
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            errors.Add("Path on the server is required.");
        }

        if (KnownHosts.Count == 0 || KnownHosts.Any(k => string.IsNullOrWhiteSpace(k) || k.Contains('\n', StringComparison.Ordinal)))
        {
            errors.Add("The server's host keys (known_hosts lines) are required.");
        }

        if ((Password is null) == (PrivateKey is null))
        {
            errors.Add("Give either a password or a private key.");
        }

        return errors;
    }
}

/// <summary>
/// A bucket (and optional prefix) on Amazon S3 or an S3-compatible service. <see cref="Endpoint"/> is host[:port]
/// without a scheme, for example s3.eu-central-1.amazonaws.com.
/// </summary>
public sealed record S3Destination(Guid RepositoryId, string Endpoint, string Bucket, string Prefix, string AccessKeyId, SecureSecret SecretAccessKey)
    : DestinationSpec(RepositoryId)
{
    public string? Region { get; init; }

    /// <summary>False only for a local service without HTTPS.</summary>
    public bool UseTls { get; init; } = true;

    public override string DisplayLocation => $"s3://{Bucket}/{Prefix} at {Endpoint}";

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint.Contains("://", StringComparison.Ordinal) || Endpoint.Contains('/', StringComparison.Ordinal))
        {
            errors.Add("Endpoint must be a host name with an optional port, without https:// or a path.");
        }

        if (string.IsNullOrWhiteSpace(Bucket))
        {
            errors.Add("Bucket is required.");
        }

        if (string.IsNullOrWhiteSpace(AccessKeyId))
        {
            errors.Add("Access key ID is required.");
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
