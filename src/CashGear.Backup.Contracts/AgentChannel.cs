using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Grpc.Net.Client;

namespace CashGear.Backup.Contracts;

/// <summary>gRPC channels to the agent over its local named pipe. There is no TCP endpoint.</summary>
[SupportedOSPlatform("windows")]
public static class AgentChannel
{
    /// <summary>The pipe the installed agent listens on.</summary>
    public const string DefaultPipeName = "CashGear.Backup.Agent";

    /// <summary>The Windows service the installer registers for the agent.</summary>
    public const string ServiceName = "CashGearBackupAgent";

    /// <summary>
    /// What a client asks for when it opens the pipe: read and write data, nothing else. Opening with
    /// GENERIC_WRITE would also ask for FILE_CREATE_PIPE_INSTANCE, which the agent grants to nobody but
    /// itself, SYSTEM and administrators (a user with it could serve a rogue instance of the pipe).
    /// </summary>
    public const PipeAccessRights ClientAccessRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <summary>
    /// A channel to the agent's pipe. The client lets the agent identify it (to decide what it may do) but
    /// not act as it: impersonation level Identification.
    /// </summary>
    public static GrpcChannel Create(string pipeName = DefaultPipeName) => Create(new AgentChannelOptions { PipeName = pipeName });

    /// <inheritdoc cref="Create(string)"/>
    public static GrpcChannel Create(AgentChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var timeout = options.ConnectTimeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)options.ConnectTimeout.TotalMilliseconds;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", options.PipeName, ClientAccessRights, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification, HandleInheritability.None);
                try
                {
                    await pipe.ConnectAsync(timeout, ct);
                    if (options.VerifyServer is { } verify)
                    {
                        await verify(pipe, ct);
                    }

                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };

        // The host name is never resolved: every connection goes through the pipe above.
        return GrpcChannel.ForAddress("http://cashgear-agent", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = options.MaxReceiveMessageSize,
        });
    }
}

/// <summary>How <see cref="AgentChannel.Create(AgentChannelOptions)"/> connects.</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentChannelOptions
{
    public string PipeName { get; init; } = AgentChannel.DefaultPipeName;

    /// <summary>
    /// How long to wait for the pipe. Without a limit, a call waits for as long as the agent is not running
    /// (the pipe does not exist), so a client that wants to say "not running" sets one.
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Runs on every new connection before any request is sent on it; throwing refuses the server. Clients that
    /// send passwords use it to check that the process serving the pipe is the agent (pipe squatting).
    /// </summary>
    public Func<NamedPipeClientStream, CancellationToken, ValueTask>? VerifyServer { get; init; }

    /// <summary>gRPC's default is 4 MB; large folder listings can exceed it.</summary>
    public int? MaxReceiveMessageSize { get; init; } = 4 * 1024 * 1024;
}
