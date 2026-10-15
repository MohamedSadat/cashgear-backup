using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using CashGear.Backup.Agent.Api;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;

namespace CashGear.Backup.Agent.Security;

internal enum AccessLevel
{
    /// <summary>Status, lists, progress, browsing: anyone the pipe ACL lets in.</summary>
    Read,

    /// <summary>Run an existing job now, cancel a run.</summary>
    Operate,

    /// <summary>Anything that makes the agent read or write folders the caller names: destinations, jobs, restore.</summary>
    Manage,
}

/// <summary>Who called, as far as the named pipe tells us.</summary>
internal sealed record CallerInfo(string? UserName, bool IsElevatedAdministrator)
{
    public static CallerInfo Unknown { get; } = new(null, false);
}

/// <summary>
/// Identifies the client of a gRPC call from its named-pipe token and enforces <see cref="AccessLevel"/>.
/// The agent runs as LocalSystem, so a Manage call is as powerful as the account behind it: by default only
/// an elevated administrator (or SYSTEM) may make one.
/// </summary>
internal sealed class CallerAuthorization(AgentOptions options)
{
    public CallerInfo Identify(ServerCallContext context)
    {
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        return pipe is null ? CallerInfo.Unknown : IdentifyOnIsolatedThread(pipe);
    }

    public bool CanManage(CallerInfo caller) =>
        caller.IsElevatedAdministrator || (options.AllowNonElevatedManage && caller.UserName is not null);

    public void Require(ServerCallContext context, AccessLevel level)
    {
        if (level == AccessLevel.Manage && !CanManage(Identify(context)))
        {
            throw new AgentException(AgentError.PermissionDenied, "This needs an elevated administrator: restart the app as administrator.");
        }
    }

    /// <summary>
    /// Impersonating the pipe client on a request thread is not safe on .NET 10: after RunAsClient returns, the
    /// client's identification-level token turns up again in later async continuations of the request, where
    /// every file or pipe access then fails (ERROR_BAD_IMPERSONATION_LEVEL). So impersonate only on a throwaway
    /// thread that does not flow or capture the request's execution context, and only to copy the token.
    /// </summary>
    private static CallerInfo IdentifyOnIsolatedThread(NamedPipeServerStream pipe)
    {
        WindowsIdentity? identity = null;
        var thread = new Thread(() =>
        {
            try
            {
                pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                // The client did not allow identification: it gets the least access.
            }
        })
        {
            IsBackground = true,
            Name = "pipe caller identification",
        };

        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }

        thread.Join();
        if (identity is null)
        {
            return CallerInfo.Unknown;
        }

        using (identity)
        {
            // With UAC, a non-elevated administrator's token has Administrators as deny-only: IsInRole is false.
            return identity.IsAnonymous
                ? CallerInfo.Unknown
                : new CallerInfo(identity.Name, identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        }
    }
}
