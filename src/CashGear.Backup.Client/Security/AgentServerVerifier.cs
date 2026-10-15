using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using CashGear.Backup.Contracts;

namespace CashGear.Backup.Client.Security;

/// <summary>Decides whether the process serving the agent's pipe really is the agent.</summary>
public interface IAgentServerVerifier
{
    /// <summary>Throws <see cref="AgentServerNotTrustedException"/> when it is not.</summary>
    ValueTask VerifyAsync(NamedPipeClientStream pipe, CancellationToken ct);
}

/// <summary>The process serving the agent's pipe is not the agent; nothing was sent to it.</summary>
public sealed class AgentServerNotTrustedException(string message) : Exception(message);

/// <summary>Which processes may serve the agent's pipe.</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentServerTrust
{
    /// <summary>The installed agent: the process the service control manager runs for this service.</summary>
    public string ServiceName { get; init; } = AgentChannel.ServiceName;

    /// <summary>Any process running as LocalSystem (part of the OS's trusted base anyway).</summary>
    public bool LocalSystem { get; init; } = true;

    /// <summary>
    /// Development only: an agent run as a console app under the same account as this app. Never in a shipped
    /// build: any program of the user could then answer on the agent's pipe while the service is stopped.
    /// </summary>
    public bool SameUser { get; init; }

    /// <summary>Test seam: process ids trusted as the agent, such as a test process hosting one in-process.</summary>
    public IReadOnlyCollection<int> ProcessIds { get; init; } = [];
}

/// <summary>
/// Client side of the pipe-squatting protection. The agent's pipe ACL stops users from adding instances while
/// the agent runs, but while it is stopped any process could create a pipe with its name and wait for the app
/// to send it a repository password. So every connection is checked before a request goes out on it: the
/// process at the other end must be the CashGearBackupAgent service (or run as LocalSystem).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentServerVerifier(AgentServerTrust? trust = null) : IAgentServerVerifier
{
    private readonly AgentServerTrust _trust = trust ?? new AgentServerTrust();

    public ValueTask VerifyAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var serverProcessId = PipeProcesses.ServerProcessId(pipe.SafePipeHandle);
        if (!IsTrusted(serverProcessId))
        {
            throw new AgentServerNotTrustedException(
                $"The program answering on the backup service's connection (process {serverProcessId}) is not the CashGear Backup service, so nothing was sent to it.");
        }

        return ValueTask.CompletedTask;
    }

    internal bool IsTrusted(int processId)
    {
        if (_trust.ProcessIds.Contains(processId) || PipeProcesses.ServiceProcessId(_trust.ServiceName) == processId)
        {
            return true;
        }

        if (!_trust.LocalSystem && !_trust.SameUser)
        {
            return false;
        }

        // Opening another account's process token is often denied; then only the service check above counts.
        var user = PipeProcesses.ProcessUser(processId);
        return user is not null
            && ((_trust.LocalSystem && user.IsWellKnown(WellKnownSidType.LocalSystemSid))
                || (_trust.SameUser && user == WindowsIdentity.GetCurrent().User));
    }
}
