using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CashGear.Backup.Client.Elevation.V1;
using CashGear.Backup.Client.Security;
using Google.Protobuf;
using Grpc.Core;

namespace CashGear.Backup.Client.Elevation;

/// <summary>
/// The app's side of the elevated helper. The first Manage call creates a private pipe, starts the helper
/// (one UAC prompt) and checks that the process that connects is that helper; later calls reuse it until it
/// has been idle for <see cref="ReuseFor"/>, so setting up a destination and a job costs one prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedSession(IHelperLauncher launcher, TimeProvider? time = null) : IManageRelay, IAsyncDisposable
{
    /// <summary>The helper exits after <see cref="ElevatedHelper.IdleTimeout"/>; the app stops reusing it well before.</summary>
    public static readonly TimeSpan ReuseFor = ElevatedHelper.IdleTimeout - TimeSpan.FromMinutes(1);

    /// <summary>How long the helper has to connect once the user approved the prompt.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private DateTimeOffset _lastUsed;

    public async Task<TResponse> InvokeAsync<TResponse>(string method, IMessage request, MessageParser<TResponse> parser, CancellationToken ct)
        where TResponse : class, IMessage<TResponse>
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parser);
        await _gate.WaitAsync(ct);
        try
        {
            if (_pipe is not null && _time.GetUtcNow() - _lastUsed > ReuseFor)
            {
                await CloseAsync();
            }

            _pipe ??= await StartHelperAsync(ct);
            ElevatedReply? reply;
            try
            {
                await ElevationFraming.WriteAsync(_pipe, new ElevatedCall { Method = method, Request = request.ToByteString() }, ct);
                reply = await ElevationFraming.ReadAsync(_pipe, ElevatedReply.Parser, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidProtocolBufferException)
            {
                // The pipe is in an unknown state: the next call starts a new helper.
                await CloseAsync();
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed, "The administrator helper stopped unexpectedly.", ex);
            }

            _lastUsed = _time.GetUtcNow();
            if (reply is null)
            {
                await CloseAsync();
                throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed, "The administrator helper closed before it answered.");
            }

            return reply.StatusCode == (int)StatusCode.OK
                ? parser.ParseFrom(reply.Response)
                : throw new AgentCallException((StatusCode)reply.StatusCode, reply.ErrorKind, reply.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NamedPipeServerStream> StartHelperAsync(CancellationToken ct)
    {
        var pipeName = "CashGear.Backup.Elevation." + RandomNumberGenerator.GetHexString(32, lowercase: true);
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, PrivatePipeSecurity());
        try
        {
            var helperProcessId = await launcher.LaunchAsync(pipeName, ct);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed, "The administrator helper did not start.");
                }
            }

            // Only the process we started may talk to us: anything else could be fishing for passwords.
            var clientProcessId = PipeProcesses.ClientProcessId(pipe.SafePipeHandle);
            if (clientProcessId != helperProcessId)
            {
                throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed,
                    $"An unexpected program (process {clientProcessId}) connected instead of the administrator helper; nothing was sent.");
            }

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private async Task CloseAsync()
    {
        if (_pipe is { } pipe)
        {
            _pipe = null;
            await pipe.DisposeAsync();
        }
    }

    /// <summary>
    /// This user and administrators (with over-the-shoulder elevation the helper runs as an administrator account)
    /// may read and write; nobody else, and nobody over the network.
    /// </summary>
    private static PipeSecurity PrivatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }

        return security;
    }
}
