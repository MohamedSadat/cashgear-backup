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
    public static GrpcChannel Create(string pipeName = DefaultPipeName)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, ClientAccessRights, PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification, HandleInheritability.None);
                try
                {
                    await pipe.ConnectAsync(ct);
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
        return GrpcChannel.ForAddress("http://cashgear-agent", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }
}
