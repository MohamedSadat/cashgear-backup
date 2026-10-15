using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using CashGear.Backup.Client.Elevation.V1;
using CashGear.Backup.Client.Security;
using CashGear.Backup.Contracts;
using CashGear.Backup.Contracts.V1;
using Google.Protobuf;
using Grpc.Core;

namespace CashGear.Backup.Client.Elevation;

/// <summary>
/// The elevated helper: the desktop app started again with <see cref="HelperArguments.Switch"/> after a UAC
/// prompt. It shows no window. It connects to the app's private pipe, checks the pipe is served by the app
/// that started it, and forwards each Manage call to the agent on its own connection, which carries the
/// elevated token the agent asks for. It forwards nothing but the Manage calls and logs nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedHelper
{
    /// <summary>Without a call for this long the helper exits; the next Manage call asks for approval again.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    public const int ExitOk = 0;
    public const int ExitBadArguments = 2;
    public const int ExitNoApp = 3;
    public const int ExitWrongApp = 4;

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs until the app closes the pipe, the helper has been idle for <see cref="IdleTimeout"/>, or <paramref name="ct"/> fires.</summary>
    public static async Task<int> RunAsync(HelperArguments arguments, IAgentServerVerifier verifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(verifier);
        await using var app = new NamedPipeClientStream(".", arguments.PipeName, AgentChannel.ClientAccessRights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await app.ConnectAsync(s_connectTimeout, ct);
        }
        catch (TimeoutException)
        {
            return ExitNoApp;
        }

        if (PipeProcesses.ServerProcessId(app.SafePipeHandle) != arguments.ParentProcessId)
        {
            return ExitWrongApp;
        }

        using var channel = AgentChannel.Create(new AgentChannelOptions
        {
            PipeName = arguments.AgentPipeName,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            VerifyServer = verifier.VerifyAsync,
        });
        var agent = new BackupAgent.BackupAgentClient(channel);
        while (true)
        {
            ElevatedCall? call;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                try
                {
                    call = await ElevationFraming.ReadAsync(app, ElevatedCall.Parser, idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return ExitOk;
                }
                catch (Exception ex) when (ex is IOException or InvalidProtocolBufferException)
                {
                    return ExitOk;
                }
            }

            if (call is null)
            {
                return ExitOk;
            }

            var reply = await RelayAsync(agent, call, ct);
            try
            {
                await ElevationFraming.WriteAsync(app, reply, ct);
            }
            catch (IOException)
            {
                return ExitOk;
            }
        }
    }

    internal static async Task<ElevatedReply> RelayAsync(BackupAgent.BackupAgentClient agent, ElevatedCall call, CancellationToken ct)
    {
        try
        {
            IMessage response = call.Method switch
            {
                ManageCalls.CreateDestination => await agent.CreateDestinationAsync(CreateDestinationRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.ConnectDestination => await agent.ConnectDestinationAsync(ConnectDestinationRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.CreateJob => await agent.CreateJobAsync(CreateJobRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.UpdateJob => await agent.UpdateJobAsync(UpdateJobRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.DeleteJob => await agent.DeleteJobAsync(DeleteJobRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.StartRestore => await agent.StartRestoreAsync(StartRestoreRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.UpdateSettings => await agent.UpdateSettingsAsync(UpdateSettingsRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.SendTestEmail => await agent.SendTestEmailAsync(SendTestEmailRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.ActivateLicence => await agent.ActivateLicenceAsync(ActivateLicenceRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.RemoveLicence => await agent.RemoveLicenceAsync(RemoveLicenceRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                ManageCalls.CreateSupportBundle => await agent.CreateSupportBundleAsync(CreateSupportBundleRequest.Parser.ParseFrom(call.Request), cancellationToken: ct),
                _ => throw new AgentCallException(StatusCode.Unimplemented, AgentErrorKinds.InvalidArgument, "The administrator helper only makes setup and restore calls."),
            };
            return new ElevatedReply { Response = response.ToByteString() };
        }
        catch (RpcException ex)
        {
            return Failure(AgentCallException.FromRpc(ex));
        }
        catch (AgentCallException ex)
        {
            return Failure(ex);
        }
        catch (InvalidProtocolBufferException)
        {
            return Failure(new AgentCallException(StatusCode.InvalidArgument, AgentErrorKinds.InvalidArgument, "The administrator helper received a malformed request."));
        }
    }

    private static ElevatedReply Failure(AgentCallException ex) =>
        new() { StatusCode = (int)(ex.Code == StatusCode.OK ? StatusCode.Unknown : ex.Code), ErrorKind = ex.Kind, Message = ex.Message };
}
