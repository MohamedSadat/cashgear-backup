using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Client.Elevation;
using CashGear.Backup.Client.Security;
using Google.Protobuf;

namespace CashGear.Backup.Client.Tests.Infrastructure;

internal static class TestClients
{
    /// <summary>The in-process agent runs in this test process, as this user: trust exactly this process.</summary>
    public static AgentServerVerifier TrustThisProcess() => new(new AgentServerTrust { ProcessIds = [Environment.ProcessId] });

    public static AgentClient For(AgentHarness agent, IManageRelay? relay = null, IAgentServerVerifier? verifier = null) =>
        new(new AgentClientOptions { PipeName = agent.PipeName, Verifier = verifier ?? TrustThisProcess() }, relay ?? new NoRelay());

    public static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> items)
    {
        var list = new List<T>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    /// <summary>For tests where the agent lets this process manage: the relay must not be used.</summary>
    private sealed class NoRelay : IManageRelay
    {
        public Task<TResponse> InvokeAsync<TResponse>(string method, IMessage request, MessageParser<TResponse> parser, CancellationToken ct)
            where TResponse : class, IMessage<TResponse> =>
            throw new InvalidOperationException($"{method} went to the elevated helper although the connection may manage.");
    }
}

/// <summary>A relay that records what reaches it and answers from a function.</summary>
internal sealed class RecordingRelay(Func<string, IMessage, IMessage> answer) : IManageRelay
{
    public List<(string Method, IMessage Request)> Calls { get; } = [];

    public Task<TResponse> InvokeAsync<TResponse>(string method, IMessage request, MessageParser<TResponse> parser, CancellationToken ct)
        where TResponse : class, IMessage<TResponse>
    {
        Calls.Add((method, request));
        return Task.FromResult((TResponse)answer(method, request));
    }
}

/// <summary>
/// Stands in for the UAC launch: runs the helper in this process (so its process id is ours) on the agent's
/// test pipe. <paramref name="reportedProcessId"/> lets a test claim a different helper process.
/// </summary>
internal sealed class InProcessLauncher(string agentPipeName, IAgentServerVerifier verifier, int? reportedProcessId = null) : IHelperLauncher
{
    public int Launches { get; private set; }

    public Task<int>? Helper { get; private set; }

    public Task<int> LaunchAsync(string pipeName, CancellationToken ct)
    {
        Launches++;
        var arguments = new HelperArguments(pipeName, Environment.ProcessId) { AgentPipeName = agentPipeName };
        Helper = Task.Run(() => ElevatedHelper.RunAsync(arguments, verifier, TestContext.Current.CancellationToken), CancellationToken.None);
        return Task.FromResult(reportedProcessId ?? Environment.ProcessId);
    }
}

/// <summary>The user says no to the UAC prompt.</summary>
internal sealed class DecliningLauncher : IHelperLauncher
{
    public Task<int> LaunchAsync(string pipeName, CancellationToken ct) =>
        throw new AgentCallException(Grpc.Core.StatusCode.Cancelled, AgentErrorKinds.ElevationCanceled, "Administrator approval was not given, so nothing was changed.");
}
