using Google.Protobuf;

namespace CashGear.Backup.Client.Elevation;

/// <summary>The agent's Manage calls: the only ones the elevated helper forwards.</summary>
public static class ManageCalls
{
    public const string CreateDestination = nameof(CreateDestination);
    public const string ConnectDestination = nameof(ConnectDestination);
    public const string CreateJob = nameof(CreateJob);
    public const string UpdateJob = nameof(UpdateJob);
    public const string DeleteJob = nameof(DeleteJob);
    public const string StartRestore = nameof(StartRestore);
    public const string UpdateSettings = nameof(UpdateSettings);
    public const string SendTestEmail = nameof(SendTestEmail);
    public const string ActivateLicence = nameof(ActivateLicence);
    public const string RemoveLicence = nameof(RemoveLicence);
    public const string CreateSupportBundle = nameof(CreateSupportBundle);
}

/// <summary>Makes Manage calls on a connection that may make them: the elevated helper's.</summary>
public interface IManageRelay
{
    /// <summary>Sends one Manage call; failures are <see cref="AgentCallException"/>s.</summary>
    Task<TResponse> InvokeAsync<TResponse>(string method, IMessage request, MessageParser<TResponse> parser, CancellationToken ct)
        where TResponse : class, IMessage<TResponse>;
}
