using CashGear.Backup.Client.Security;
using Grpc.Core;

namespace CashGear.Backup.Client;

/// <summary>
/// A call to the agent failed. <see cref="Kind"/> is the agent's "cashgear-error-kind" trailer (see
/// <see cref="AgentErrorKinds"/>) or one the client adds for connection and elevation problems. The message is
/// meant for the user: the agent's own messages never contain secrets.
/// </summary>
public sealed class AgentCallException : Exception
{
    /// <summary>The trailer the agent puts the error kind in.</summary>
    public const string ErrorKindTrailer = "cashgear-error-kind";

    public AgentCallException(StatusCode code, string kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Kind = kind;
    }

    public StatusCode Code { get; }

    public string Kind { get; }

    /// <summary>The agent could not be reached or could not be trusted, so nothing was sent to it.</summary>
    public bool IsConnectionProblem => Kind is AgentErrorKinds.AgentNotRunning or AgentErrorKinds.AgentNotTrusted;

    public static AgentCallException FromRpc(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var kind = ex.Trailers.GetValue(ErrorKindTrailer);
        if (!string.IsNullOrEmpty(kind))
        {
            return new AgentCallException(ex.StatusCode, kind, ex.Status.Detail, ex);
        }

        if (FindInChain<AgentServerNotTrustedException>(ex) is { } untrusted)
        {
            return new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.AgentNotTrusted, untrusted.Message, ex);
        }

        // A connection that never came up (no pipe within the connect timeout) surfaces as "Internal" with an
        // HttpRequestException inside, not as "Unavailable".
        var notConnected = FindInChain<HttpRequestException>(ex) is not null;
        return ex.StatusCode switch
        {
            _ when notConnected => new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.AgentNotRunning,
                "The CashGear Backup service is not running or did not answer.", ex),
            StatusCode.Unavailable or StatusCode.DeadlineExceeded => new AgentCallException(ex.StatusCode, AgentErrorKinds.AgentNotRunning,
                "The CashGear Backup service is not running or did not answer.", ex),
            StatusCode.PermissionDenied => new AgentCallException(ex.StatusCode, AgentErrorKinds.PermissionDenied, ex.Status.Detail, ex),
            _ => new AgentCallException(ex.StatusCode, AgentErrorKinds.Internal,
                string.IsNullOrEmpty(ex.Status.Detail) ? "The backup service hit an unexpected error." : ex.Status.Detail, ex),
        };
    }

    private static T? FindInChain<T>(RpcException ex) where T : Exception
    {
        for (var e = ex.Status.DebugException ?? ex.InnerException; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}

/// <summary>Error kinds the UI acts on. The agent's own are the names of its error and engine error enums.</summary>
public static class AgentErrorKinds
{
    // Added by the client.
    public const string AgentNotRunning = nameof(AgentNotRunning);
    public const string AgentNotTrusted = nameof(AgentNotTrusted);
    public const string ElevationCanceled = nameof(ElevationCanceled);
    public const string ElevationFailed = nameof(ElevationFailed);
    public const string Internal = nameof(Internal);

    // Sent by the agent.
    public const string InvalidArgument = nameof(InvalidArgument);
    public const string NotFound = nameof(NotFound);
    public const string AlreadyExists = nameof(AlreadyExists);
    public const string FailedPrecondition = nameof(FailedPrecondition);
    public const string PermissionDenied = nameof(PermissionDenied);
    public const string InvalidPassword = nameof(InvalidPassword);
    public const string RepositoryNotFound = nameof(RepositoryNotFound);
    public const string RepositoryAlreadyExists = nameof(RepositoryAlreadyExists);
    public const string DestinationUnreachable = nameof(DestinationUnreachable);
    public const string SourceUnavailable = nameof(SourceUnavailable);
    public const string RestoreTargetNotEmpty = nameof(RestoreTargetNotEmpty);
    public const string RunAlreadyActive = nameof(RunAlreadyActive);
    public const string EngineUnavailable = nameof(EngineUnavailable);
    public const string LicenceRequired = nameof(LicenceRequired);
}
