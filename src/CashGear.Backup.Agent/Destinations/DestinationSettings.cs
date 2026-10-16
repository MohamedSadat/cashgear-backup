using System.Text.Json;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Core;
using CashGear.Backup.Data;

namespace CashGear.Backup.Agent.Destinations;

/// <summary>
/// The non-secret connection settings of a destination, stored as JSON in state.db. The matching credential (share
/// or SFTP password, SFTP private key, S3 secret key) is stored apart, DPAPI-protected.
/// </summary>
internal sealed record DestinationSettings
{
    private static readonly JsonSerializerOptions s_json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Share and SFTP: the account.</summary>
    public string? Username { get; init; }

    // SFTP
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? RemotePath { get; init; }
    public IReadOnlyList<string>? KnownHosts { get; init; }

    /// <summary>SFTP: the credential is a private key rather than a password.</summary>
    public bool PrivateKey { get; init; }

    // S3
    public string? Endpoint { get; init; }
    public string? Bucket { get; init; }
    public string? Prefix { get; init; }
    public string? Region { get; init; }
    public string? AccessKeyId { get; init; }
    public bool InsecureHttp { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static DestinationSettings FromJson(string? json) =>
        string.IsNullOrEmpty(json) ? new DestinationSettings() : JsonSerializer.Deserialize<DestinationSettings>(json, s_json) ?? new DestinationSettings();
}

/// <summary>A destination to add: the request as the agent understands it. The caller disposes <see cref="Credential"/>.</summary>
internal sealed record DestinationInput(string Name, DestinationKind Kind, string Location, DestinationSettings Settings, SecureSecret? Credential)
{
    /// <summary>Checks the kind-specific settings and returns where the destination is (the unique location in state.db).</summary>
    public string Validate()
    {
        switch (Kind)
        {
            case DestinationKind.FileSystem:
                return AbsoluteFolder(Location);
            case DestinationKind.NetworkShare:
                var share = AbsoluteFolder(Location);
                if (ShareConnection.ShareRoot(share) is null)
                {
                    throw new AgentException(AgentError.InvalidArgument, "A network share must be a path like \\\\server\\share\\folder.");
                }

                if (string.IsNullOrWhiteSpace(Settings.Username) || Credential is null)
                {
                    throw new AgentException(AgentError.InvalidArgument, "Enter the account and password for the network share.");
                }

                return share;
            case DestinationKind.Sftp:
                if (Credential is null)
                {
                    throw new AgentException(AgentError.InvalidArgument, "Enter the SFTP password or private key.");
                }

                return Require(Spec(Guid.NewGuid(), Credential)).DisplayLocation;
            case DestinationKind.S3:
                if (Credential is null)
                {
                    throw new AgentException(AgentError.InvalidArgument, "Enter the S3 secret access key.");
                }

                return Require(Spec(Guid.NewGuid(), Credential)).DisplayLocation;
            default:
                throw new AgentException(AgentError.InvalidArgument, "Choose a destination kind.");
        }
    }

    /// <summary>The engine's view of an SFTP or S3 destination; <paramref name="credential"/> stays owned by the caller.</summary>
    public DestinationSpec Spec(Guid id, SecureSecret credential) => Kind switch
    {
        DestinationKind.Sftp => new SftpDestination(id, Settings.Host ?? "", Settings.Port == 0 ? 22 : Settings.Port, Settings.Username ?? "",
            Settings.RemotePath ?? "", Settings.KnownHosts ?? [])
        {
            Password = Settings.PrivateKey ? null : credential,
            PrivateKey = Settings.PrivateKey ? credential : null,
        },
        DestinationKind.S3 => new S3Destination(id, Settings.Endpoint ?? "", Settings.Bucket ?? "", Settings.Prefix ?? "", Settings.AccessKeyId ?? "", credential)
        {
            Region = string.IsNullOrWhiteSpace(Settings.Region) ? null : Settings.Region,
            UseTls = !Settings.InsecureHttp,
        },
        _ => throw new InvalidOperationException($"{Kind} has no remote settings."),
    };

    private static DestinationSpec Require(DestinationSpec spec)
    {
        var errors = spec.Validate();
        return errors.Count == 0 ? spec : throw new AgentException(AgentError.InvalidArgument, string.Join(" ", errors));
    }

    private static string AbsoluteFolder(string location) =>
        string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location)
            ? throw new AgentException(AgentError.InvalidArgument, "The destination must be an absolute folder path.")
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
}
