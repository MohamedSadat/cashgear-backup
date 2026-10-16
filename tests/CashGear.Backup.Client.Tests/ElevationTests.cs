using System.IO.Pipes;
using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Client.Elevation;
using CashGear.Backup.Client.Elevation.V1;
using CashGear.Backup.Client.Tests.Infrastructure;
using CashGear.Backup.Contracts;
using CashGear.Backup.Contracts.V1;
using Google.Protobuf;
using Grpc.Core;
using static CashGear.Backup.Client.Tests.Infrastructure.TestClients;

namespace CashGear.Backup.Client.Tests;

/// <summary>
/// The elevated helper relay without a real UAC prompt: the "helper" runs in this process, so the process-id
/// checks on both ends see the ids they expect.
/// </summary>
[Trait("Category", "Integration")]
public class ElevationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Manage_calls_travel_through_one_helper_and_come_back()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(Manage_calls_travel_through_one_helper_and_come_back));
        var launcher = new InProcessLauncher(agent.PipeName, TrustThisProcess());
        await using var session = new ElevatedSession(launcher);

        var destination = await session.InvokeAsync(ManageCalls.CreateDestination, new CreateDestinationRequest
        {
            Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = agent.Password,
        }, Destination.Parser, Ct);
        Assert.Equal(DestinationState.Connected, destination.State);

        var job = await session.InvokeAsync(ManageCalls.CreateJob, new CreateJobRequest { Spec = AgentHarness.Spec(destination.Id, agent.Source) }, Job.Parser, Ct);
        Assert.Equal("Documents", job.Spec.Name);
        Assert.Equal(1, launcher.Launches);

        // An agent error comes back with its kind, through the same helper.
        var duplicate = await Assert.ThrowsAsync<AgentCallException>(() =>
            session.InvokeAsync(ManageCalls.CreateJob, new CreateJobRequest { Spec = AgentHarness.Spec(destination.Id, agent.Source) }, Job.Parser, Ct));
        Assert.Equal(AgentErrorKinds.AlreadyExists, duplicate.Kind);
        Assert.Equal(StatusCode.AlreadyExists, duplicate.Code);
        Assert.Equal(1, launcher.Launches);

        using var client = For(agent);
        Assert.Equal(job.Id, Assert.Single(await client.ListJobsAsync(Ct)).Id);

        // Settings (with the email password) go the same way; the password does not come back.
        var settings = await client.GetSettingsAsync(Ct);
        settings.AlertAfterDays = 5;
        settings.Email = new EmailSettings { SmtpHost = "mail.example.test", Security = SmtpSecurity.Starttls, Username = "backup", Password = "mail-Secret-1" };
        var saved = await session.InvokeAsync(ManageCalls.UpdateSettings, new UpdateSettingsRequest { Settings = settings }, Settings.Parser, Ct);
        Assert.Equal(5, saved.AlertAfterDays);
        Assert.True(saved.Email.PasswordSaved);
        Assert.Equal("", saved.Email.Password);
        Assert.Equal(1, launcher.Launches);

        // Licence and support calls too.
        var licence = await session.InvokeAsync(ManageCalls.RemoveLicence, new RemoveLicenceRequest(), LicenceStatus.Parser, Ct);
        Assert.True(licence.BackupsAllowed);
        var bundle = await session.InvokeAsync(ManageCalls.CreateSupportBundle, new CreateSupportBundleRequest(), SupportBundle.Parser, Ct);
        Assert.False(bundle.Content.IsEmpty);
        Assert.Equal(1, launcher.Launches);

        // Closing the session ends the helper.
        await session.DisposeAsync();
        Assert.Equal(ElevatedHelper.ExitOk, await launcher.Helper!.WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task A_declined_prompt_changes_nothing()
    {
        await using var session = new ElevatedSession(new DecliningLauncher());
        var ex = await Assert.ThrowsAsync<AgentCallException>(() =>
            session.InvokeAsync(ManageCalls.DeleteJob, new DeleteJobRequest { Id = Guid.NewGuid().ToString() }, DeleteJobResponse.Parser, Ct));
        Assert.Equal(AgentErrorKinds.ElevationCanceled, ex.Kind);
    }

    [Fact]
    public async Task The_app_refuses_a_helper_pipe_client_it_did_not_start()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(The_app_refuses_a_helper_pipe_client_it_did_not_start));
        var launcher = new InProcessLauncher(agent.PipeName, TrustThisProcess(), reportedProcessId: int.MaxValue);
        await using var session = new ElevatedSession(launcher);

        var ex = await Assert.ThrowsAsync<AgentCallException>(() => session.InvokeAsync(ManageCalls.CreateDestination, new CreateDestinationRequest
        {
            Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = agent.Password,
        }, Destination.Parser, Ct));
        Assert.Equal(AgentErrorKinds.ElevationFailed, ex.Kind);
        Assert.Contains("unexpected program", ex.Message, StringComparison.Ordinal);

        // The password never left: the helper saw the pipe close before any call.
        Assert.Equal(ElevatedHelper.ExitOk, await launcher.Helper!.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        using var client = For(agent);
        Assert.Empty(await client.ListDestinationsAsync(Ct));
    }

    [Fact]
    public async Task The_helper_refuses_a_pipe_not_served_by_the_app_that_started_it()
    {
        var name = "cashgear-test-elevation-" + Guid.NewGuid().ToString("N");
        await using var rogue = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = rogue.WaitForConnectionAsync(Ct);

        var exit = await ElevatedHelper.RunAsync(new HelperArguments(name, ParentProcessId: Environment.ProcessId + 1), TrustThisProcess(), Ct);

        Assert.Equal(ElevatedHelper.ExitWrongApp, exit);
        await waiting;
    }

    [Fact]
    public async Task The_helper_forwards_only_manage_calls()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(The_helper_forwards_only_manage_calls));
        using var channel = AgentChannel.Create(agent.PipeName);
        var reply = await ElevatedHelper.RelayAsync(new BackupAgent.BackupAgentClient(channel),
            new ElevatedCall { Method = "ListJobs", Request = new ListJobsRequest().ToByteString() }, Ct);

        Assert.Equal((int)StatusCode.Unimplemented, reply.StatusCode);
        Assert.True(reply.Response.IsEmpty);
    }

    [Fact]
    public async Task The_client_sends_manage_calls_to_the_helper_when_the_agent_says_this_connection_may_not()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(The_client_sends_manage_calls_to_the_helper_when_the_agent_says_this_connection_may_not),
            o => o.AllowNonElevatedManage = false);
        using (var probe = For(agent))
        {
            Assert.SkipWhen((await probe.GetStatusAsync(Ct)).CallerCanManage, "The tests run elevated: this connection may manage.");
        }

        var relay = new RecordingRelay((method, _) => method == ManageCalls.DeleteJob ? new DeleteJobResponse() : new Job { Id = "from-helper" });
        using var client = For(agent, relay);

        Assert.Equal("from-helper", (await client.CreateJobAsync(AgentHarness.Spec(Guid.NewGuid().ToString(), agent.Source), Ct)).Id);
        await client.DeleteJobAsync(Guid.NewGuid().ToString(), Ct);
        Assert.Equal([ManageCalls.CreateJob, ManageCalls.DeleteJob], relay.Calls.Select(c => c.Method));

        // Read and Operate calls stay on the app's own connection.
        Assert.Empty(await client.ListJobsAsync(Ct));
        Assert.Equal(2, relay.Calls.Count);
    }

    [Theory]
    [InlineData(new[] { "--cashgear-elevated-helper", "--pipe", "abc.1", "--parent", "42" }, true)]
    [InlineData(new[] { "--cashgear-elevated-helper", "--pipe", "abc 1", "--parent", "42" }, false)]
    [InlineData(new[] { "--cashgear-elevated-helper", "--pipe", "abc", "--parent", "-1" }, false)]
    [InlineData(new[] { "--cashgear-elevated-helper", "--pipe", "abc" }, false)]
    [InlineData(new[] { "--cashgear-elevated-helper", "--pipe", "abc", "--parent", "42", "--password", "x" }, false)]
    [InlineData(new[] { "--pipe", "abc", "--parent", "42" }, false)]
    public void Helper_arguments_are_parsed_strictly(string[] args, bool valid)
    {
        var parsed = HelperArguments.Parse(args);
        Assert.Equal(valid, parsed is not null);
        if (parsed is not null)
        {
            Assert.Equal(parsed, HelperArguments.Parse(parsed.ToArguments()));
        }
    }
}
