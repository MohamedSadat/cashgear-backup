using System.Diagnostics;
using System.Security.Principal;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Client.Security;
using CashGear.Backup.Contracts.V1;
using static CashGear.Backup.Client.Tests.Infrastructure.TestClients;

namespace CashGear.Backup.Client.Tests;

/// <summary>Client side of the pipe-squatting protection: only the agent service may receive requests.</summary>
[Trait("Category", "Integration")]
public class AgentServerVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pipe_served_by_anything_but_the_service_gets_no_request()
    {
        // The in-process agent is a squatter as far as the shipped client is concerned: not the service, not SYSTEM.
        Assert.False(WindowsIdentity.GetCurrent().IsSystem, "run the tests as a normal account");
        await using var agent = await AgentHarness.StartAsync(nameof(A_pipe_served_by_anything_but_the_service_gets_no_request));
        using var shipped = For(agent, verifier: new AgentServerVerifier());

        var ex = await Assert.ThrowsAsync<AgentCallException>(() => shipped.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = agent.Password,
        }, Ct));
        Assert.Equal(AgentErrorKinds.AgentNotTrusted, ex.Kind);
        Assert.True(ex.IsConnectionProblem);
        Assert.Contains($"process {Environment.ProcessId}", ex.Message, StringComparison.Ordinal);

        // Nothing reached the agent.
        using var trusting = For(agent);
        Assert.Empty(await trusting.ListDestinationsAsync(Ct));
    }

    [Fact]
    public async Task Trusts_the_given_processes_and_a_same_user_agent_only_when_told()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(Trusts_the_given_processes_and_a_same_user_agent_only_when_told));

        using var byProcessId = For(agent);
        Assert.True((await byProcessId.GetStatusAsync(Ct)).Ready);

        using var sameUser = For(agent, verifier: new AgentServerVerifier(new AgentServerTrust { SameUser = true }));
        Assert.True((await sameUser.GetStatusAsync(Ct)).Ready);

        using var otherService = For(agent, verifier: new AgentServerVerifier(new AgentServerTrust { ServiceName = "RpcSs", LocalSystem = false }));
        Assert.Equal(AgentErrorKinds.AgentNotTrusted, (await Assert.ThrowsAsync<AgentCallException>(() => otherService.GetStatusAsync(Ct))).Kind);
    }

    [Fact]
    public void Finds_the_process_of_a_running_service_and_nothing_for_a_missing_one()
    {
        // RpcSs runs on every Windows machine.
        var rpc = PipeProcesses.ServiceProcessId("RpcSs");
        Assert.NotNull(rpc);
        using (var process = Process.GetProcessById(rpc.Value))
        {
            Assert.Equal("svchost", process.ProcessName, ignoreCase: true);
        }

        Assert.Null(PipeProcesses.ServiceProcessId("CashGearNoSuchService" + Guid.NewGuid().ToString("N")));
        Assert.Equal(WindowsIdentity.GetCurrent().User, PipeProcesses.ProcessUser(Environment.ProcessId));
    }

    [Fact]
    public void The_service_process_is_trusted_by_the_default_policy()
    {
        var verifier = new AgentServerVerifier(new AgentServerTrust { ServiceName = "RpcSs", LocalSystem = false });
        Assert.True(verifier.IsTrusted(PipeProcesses.ServiceProcessId("RpcSs")!.Value));
        Assert.False(verifier.IsTrusted(Environment.ProcessId));
    }
}
