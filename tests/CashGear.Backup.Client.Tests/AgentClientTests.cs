using CashGear.Backup.Agent.Tests.Infrastructure;
using CashGear.Backup.Client.Security;
using CashGear.Backup.Client.Tests.Infrastructure;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Testing;
using static CashGear.Backup.Client.Tests.Infrastructure.TestClients;

namespace CashGear.Backup.Client.Tests;

/// <summary>The desktop app's client against the real agent on a private pipe.</summary>
[Trait("Category", "Integration")]
public class AgentClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sets_up_backs_up_browses_and_restores_through_the_client()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(Sets_up_backs_up_browses_and_restores_through_the_client));
        WriteSource(agent.Source);
        var manifest = TreeManifest.Capture(agent.Source);
        using var client = For(agent);

        var status = await client.GetStatusAsync(Ct);
        Assert.True(status.Ready);
        Assert.True(status.CallerCanManage);

        var destination = await client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = agent.Password,
        }, Ct);
        Assert.Equal(DestinationState.Connected, destination.State);
        Assert.Equal(destination, Assert.Single(await client.ListDestinationsAsync(Ct)));

        var job = await client.CreateJobAsync(AgentHarness.Spec(destination.Id, agent.Source), Ct);
        Assert.Equal(job.Id, Assert.Single(await client.ListJobsAsync(Ct)).Id);

        var started = await client.RunJobNowAsync(job.Id, Ct);
        var updates = await ToListAsync(client.WatchRunAsync(started.Id, Ct));
        var backup = updates[^1].Finished;
        Assert.Equal(RunState.Succeeded, backup.State);
        Assert.Equal(manifest.FileCount, backup.Files);
        Assert.Equal(backup, Assert.Single(await client.ListRunsAsync(job.Id, 10, Ct)));

        var snapshot = Assert.Single(await client.ListSnapshotsAsync(job.Id, Ct));
        Assert.Equal(backup.SnapshotId, snapshot.Id);
        var root = await client.BrowseSnapshotAsync(job.Id, snapshot.Id, "", Ct);
        Assert.Equal(EntryKind.Directory, root.Single(e => e.Name == "docs").Kind);
        var docs = await client.BrowseSnapshotAsync(job.Id, snapshot.Id, "docs", Ct);
        Assert.Equal(["a.txt", "b.txt"], docs.Select(e => e.Name).Order());

        var target = Path.Combine(agent.Restores, "docs");
        var restore = await client.StartRestoreAsync(new StartRestoreRequest { JobId = job.Id, SnapshotId = snapshot.Id, Path = "docs", TargetFolder = target }, Ct);
        var restored = (await ToListAsync(client.WatchRunAsync(restore.Id, Ct)))[^1].Finished;
        Assert.Equal(RunState.Succeeded, restored.State);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), Ct));

        var found = new List<SnapshotEntry>();
        await foreach (var response in client.SearchSnapshotAsync(job.Id, snapshot.Id, "B.TX", 0, Ct))
        {
            found.AddRange(response.Matches);
        }

        Assert.Equal("docs/b.txt", Assert.Single(found).Path);
        var check = await client.VerifyJobNowAsync(job.Id, Ct);
        Assert.Equal(RunKind.Verify, check.Kind);
        Assert.Equal(RunState.Succeeded, (await ToListAsync(client.WatchRunAsync(check.Id, Ct)))[^1].Finished.State);
        Assert.Empty(await client.ListAlertsAsync(Ct));

        // Restores never overwrite: the same target again is refused, with a kind the UI can act on.
        var again = await Assert.ThrowsAsync<AgentCallException>(() =>
            client.StartRestoreAsync(new StartRestoreRequest { JobId = job.Id, SnapshotId = snapshot.Id, TargetFolder = target }, Ct));
        Assert.Equal(AgentErrorKinds.RestoreTargetNotEmpty, again.Kind);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), Ct));
    }

    [Fact]
    public async Task Agent_errors_arrive_with_their_kind_and_message()
    {
        await using var agent = await AgentHarness.StartAsync(nameof(Agent_errors_arrive_with_their_kind_and_message));
        using var client = For(agent);

        const string ShortPassword = "Zq7!x";
        var tooShort = await Assert.ThrowsAsync<AgentCallException>(() => client.CreateDestinationAsync(new CreateDestinationRequest
        {
            Name = "Backup disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = ShortPassword,
        }, Ct));
        Assert.Equal(AgentErrorKinds.InvalidArgument, tooShort.Kind);
        Assert.Contains("at least 8 characters", tooShort.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ShortPassword, tooShort.ToString(), StringComparison.Ordinal);

        await agent.CreateDestinationAsync();
        var twice = await Assert.ThrowsAsync<AgentCallException>(() => client.ConnectDestinationAsync(new ConnectDestinationRequest
        {
            Name = "Same disk", Kind = DestinationKind.FileSystem, Location = agent.Destination, Password = agent.Password,
        }, Ct));
        Assert.Equal(AgentErrorKinds.AlreadyExists, twice.Kind);
        Assert.DoesNotContain(agent.Password, twice.ToString(), StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<AgentCallException>(() => client.RunJobNowAsync(Guid.NewGuid().ToString(), Ct));
        Assert.Equal(AgentErrorKinds.NotFound, missing.Kind);
    }

    [Fact]
    public async Task A_missing_agent_is_reported_as_not_running_within_the_connect_timeout()
    {
        using var client = new AgentClient(new AgentClientOptions
        {
            PipeName = "cashgear-test-nobody-" + Guid.NewGuid().ToString("N"),
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            Verifier = new AgentServerVerifier(),
        }, new RecordingRelay((_, _) => throw new InvalidOperationException()));

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<AgentCallException>(() => client.GetStatusAsync(Ct));
        Assert.Equal(AgentErrorKinds.AgentNotRunning, ex.Kind);
        Assert.True(ex.IsConnectionProblem);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    internal static void WriteSource(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(root, "docs", "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(root, "top.txt"), "top");
    }
}
