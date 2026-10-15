using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using Google.Protobuf.WellKnownTypes;

namespace CashGear.Backup.UI.Components.Tests.Infrastructure;

/// <summary>
/// An in-memory agent for component tests. Every call is recorded as "Method" or "Method:argument"; a failure
/// registered under the method name is thrown instead. WatchRun streams what the test pushes with
/// <see cref="Progress"/> and <see cref="Finish"/>.
/// </summary>
internal sealed class FakeAgentClient : IAgentClient
{
    private readonly ConcurrentDictionary<string, Channel<RunProgress>> _watches = new();
    private int _ids;

    public AgentStatus Status { get; set; } = new()
    {
        AgentVersion = "1.0.0", MachineIdentity = "cg-0123456789abcdef", Ready = true, CallerCanManage = true,
        StartedAt = Timestamp.FromDateTimeOffset(Sample.Now.AddHours(-1)),
    };

    public List<Destination> Destinations { get; } = [];
    public List<Job> Jobs { get; } = [];

    /// <summary>Runs per job id, newest first.</summary>
    public Dictionary<string, List<Run>> Runs { get; } = [];

    public Dictionary<string, List<Snapshot>> Snapshots { get; } = [];
    public Dictionary<(string SnapshotId, string Path), List<SnapshotEntry>> Entries { get; } = [];

    /// <summary>Method name (for example "CreateJob") to the error it throws.</summary>
    public ConcurrentDictionary<string, AgentCallException> Failures { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public List<CreateDestinationRequest> CreatedDestinations { get; } = [];
    public List<ConnectDestinationRequest> ConnectedDestinations { get; } = [];
    public List<JobSpec> CreatedJobs { get; } = [];
    public List<(string Id, JobSpec Spec)> UpdatedJobs { get; } = [];
    public List<StartRestoreRequest> Restores { get; } = [];

    public Func<string, Run> OnRunJobNow { get; set; } = jobId => new Run
    {
        Id = "run-new", JobId = jobId, Kind = RunKind.Backup, State = RunState.Running, Trigger = RunTrigger.Manual,
        StartedAt = Timestamp.FromDateTimeOffset(Sample.Now),
    };

    public Func<StartRestoreRequest, Run> OnStartRestore { get; set; } = request => new Run
    {
        Id = "restore-new", JobId = request.JobId, Kind = RunKind.Restore, State = RunState.Running, Trigger = RunTrigger.Manual,
        StartedAt = Timestamp.FromDateTimeOffset(Sample.Now), SnapshotId = request.SnapshotId, RestoreTarget = request.TargetFolder,
    };

    public void Progress(string runId, RunProgress progress) => Watch(runId).Writer.TryWrite(progress);

    public void Finish(string runId, Run finished)
    {
        var writer = Watch(runId).Writer;
        writer.TryWrite(new RunProgress { RunId = runId, State = finished.State, Finished = finished });
        writer.TryComplete();
    }

    public bool Called(string call) => Calls.Contains(call);

    public int CountOf(string method) => Calls.Count(c => c == method || c.StartsWith(method + ":", StringComparison.Ordinal));

    public Task<AgentStatus> GetStatusAsync(CancellationToken ct) => Respond("GetStatus", () => Status.Clone());

    public Task<IReadOnlyList<Destination>> ListDestinationsAsync(CancellationToken ct) =>
        Respond<IReadOnlyList<Destination>>("ListDestinations", () => [.. Destinations]);

    public Task<Destination> CreateDestinationAsync(CreateDestinationRequest request, CancellationToken ct) => Respond("CreateDestination", () =>
    {
        CreatedDestinations.Add(request.Clone());
        var destination = new Destination
        {
            Id = $"dest-new-{++_ids}", Name = request.Name, Kind = request.Kind, Location = request.Location, State = DestinationState.Connected,
        };
        Destinations.Add(destination);
        return destination;
    });

    public Task<Destination> ConnectDestinationAsync(ConnectDestinationRequest request, CancellationToken ct) => Respond("ConnectDestination", () =>
    {
        ConnectedDestinations.Add(request.Clone());
        var destination = new Destination
        {
            Id = $"dest-connected-{++_ids}", Name = request.Name, Kind = request.Kind, Location = request.Location, State = DestinationState.Connected,
        };
        Destinations.Add(destination);
        return destination;
    });

    public Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct) => Respond<IReadOnlyList<Job>>("ListJobs", () => [.. Jobs.Select(j => j.Clone())]);

    public Task<Job> CreateJobAsync(JobSpec spec, CancellationToken ct) => Respond("CreateJob", () =>
    {
        CreatedJobs.Add(spec.Clone());
        var job = new Job { Id = $"job-new-{++_ids}", Spec = spec.Clone() };
        Jobs.Add(job);
        return job;
    });

    public Task<Job> UpdateJobAsync(string jobId, JobSpec spec, CancellationToken ct) => Respond($"UpdateJob:{jobId}", () =>
    {
        UpdatedJobs.Add((jobId, spec.Clone()));
        var job = Jobs.Single(j => j.Id == jobId);
        job.Spec = spec.Clone();
        return job.Clone();
    });

    public Task DeleteJobAsync(string jobId, CancellationToken ct) => Respond($"DeleteJob:{jobId}", () => Jobs.RemoveAll(j => j.Id == jobId));

    public Task<Run> RunJobNowAsync(string jobId, CancellationToken ct) => Respond($"RunJobNow:{jobId}", () => OnRunJobNow(jobId));

    public Task<IReadOnlyList<Run>> ListRunsAsync(string? jobId, int limit, CancellationToken ct) =>
        Respond<IReadOnlyList<Run>>($"ListRuns:{jobId}", () =>
            [.. (jobId is null ? Runs.Values.SelectMany(r => r) : Runs.GetValueOrDefault(jobId, [])).Take(limit).Select(r => r.Clone())]);

    public async IAsyncEnumerable<RunProgress> WatchRunAsync(string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        await Respond($"WatchRun:{runId}", () => 0);
        await foreach (var update in Watch(runId).Reader.ReadAllAsync(ct))
        {
            yield return update;
        }
    }

    public Task CancelRunAsync(string runId, CancellationToken ct) => Respond($"CancelRun:{runId}", () => 0);

    public Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string jobId, CancellationToken ct) =>
        Respond<IReadOnlyList<Snapshot>>($"ListSnapshots:{jobId}", () => [.. Snapshots.GetValueOrDefault(jobId, [])]);

    public Task<IReadOnlyList<SnapshotEntry>> BrowseSnapshotAsync(string jobId, string snapshotId, string path, CancellationToken ct) =>
        Respond<IReadOnlyList<SnapshotEntry>>($"BrowseSnapshot:{snapshotId}/{path}", () => [.. Entries.GetValueOrDefault((snapshotId, path), [])]);

    public Task<Run> StartRestoreAsync(StartRestoreRequest request, CancellationToken ct) => Respond("StartRestore", () =>
    {
        Restores.Add(request.Clone());
        return OnStartRestore(request);
    });

    private Channel<RunProgress> Watch(string runId) => _watches.GetOrAdd(runId, _ => Channel.CreateUnbounded<RunProgress>());

    private Task<T> Respond<T>(string call, Func<T> result)
    {
        Calls.Enqueue(call);
        var method = call.Split(':')[0];
        try
        {
            return Failures.TryGetValue(method, out var failure) ? Task.FromException<T>(failure) : Task.FromResult(result());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}
