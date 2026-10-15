namespace CashGear.Backup.Agent.Api;

internal enum AgentError
{
    InvalidArgument,
    NotFound,
    AlreadyExists,
    FailedPrecondition,
    PermissionDenied,
}

/// <summary>A request the agent refuses; becomes a gRPC status (see <see cref="ErrorInterceptor"/>).</summary>
internal sealed class AgentException(AgentError error, string message) : Exception(message)
{
    public AgentError Error { get; } = error;

    public static Guid ParseId(string? value, string what) =>
        Guid.TryParse(value, out var id) ? id : throw new AgentException(AgentError.InvalidArgument, $"'{value}' is not a valid {what} id.");
}
