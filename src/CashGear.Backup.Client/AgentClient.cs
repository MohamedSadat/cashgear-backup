using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using CashGear.Backup.Client.Elevation;
using CashGear.Backup.Client.Security;
using CashGear.Backup.Contracts;
using CashGear.Backup.Contracts.V1;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace CashGear.Backup.Client;

/// <summary>How the desktop app reaches the agent.</summary>
[SupportedOSPlatform("windows")]
public sealed class AgentClientOptions
{
    public string PipeName { get; init; } = AgentChannel.DefaultPipeName;

    /// <summary>After this long without the pipe, the agent counts as not running.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Checks every connection before anything is sent on it.</summary>
    public required IAgentServerVerifier Verifier { get; init; }
}

/// <summary>
/// <see cref="IAgentClient"/> over the agent's named pipe. Read and Operate calls go out on the app's own
/// connection. Before each Manage call it asks the agent whether this connection may make it; if not, the call
/// goes through <see cref="IManageRelay"/> (the elevated helper). Request messages are never logged or put
/// in exceptions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentClient : IAgentClient, IDisposable
{
    private static readonly TimeSpan s_statusDeadline = TimeSpan.FromSeconds(10);

    private readonly GrpcChannel _channel;
    private readonly BackupAgent.BackupAgentClient _agent;
    private readonly IManageRelay _elevated;

    public AgentClient(AgentClientOptions options, IManageRelay elevated)
    {
        ArgumentNullException.ThrowIfNull(options);
        _elevated = elevated ?? throw new ArgumentNullException(nameof(elevated));
        _channel = AgentChannel.Create(new AgentChannelOptions
        {
            PipeName = options.PipeName,
            ConnectTimeout = options.ConnectTimeout,
            VerifyServer = options.Verifier.VerifyAsync,
            MaxReceiveMessageSize = 64 * 1024 * 1024,
        });
        _agent = new BackupAgent.BackupAgentClient(_channel);
    }

    public Task<AgentStatus> GetStatusAsync(CancellationToken ct) =>
        CallAsync(o => _agent.GetStatusAsync(new GetStatusRequest(), o.WithDeadline(DateTime.UtcNow + s_statusDeadline)), ct);

    public async Task<IReadOnlyList<Destination>> ListDestinationsAsync(CancellationToken ct) =>
        (await CallAsync(o => _agent.ListDestinationsAsync(new ListDestinationsRequest(), o), ct)).Destinations;

    public Task<Destination> CreateDestinationAsync(CreateDestinationRequest request, CancellationToken ct) =>
        ManageAsync(ManageCalls.CreateDestination, request, Destination.Parser, o => _agent.CreateDestinationAsync(request, o), ct);

    public Task<Destination> ConnectDestinationAsync(ConnectDestinationRequest request, CancellationToken ct) =>
        ManageAsync(ManageCalls.ConnectDestination, request, Destination.Parser, o => _agent.ConnectDestinationAsync(request, o), ct);

    public async Task<IReadOnlyList<Job>> ListJobsAsync(CancellationToken ct) =>
        (await CallAsync(o => _agent.ListJobsAsync(new ListJobsRequest(), o), ct)).Jobs;

    public Task<Job> CreateJobAsync(JobSpec spec, CancellationToken ct)
    {
        var request = new CreateJobRequest { Spec = spec };
        return ManageAsync(ManageCalls.CreateJob, request, Job.Parser, o => _agent.CreateJobAsync(request, o), ct);
    }

    public Task<Job> UpdateJobAsync(string jobId, JobSpec spec, CancellationToken ct)
    {
        var request = new UpdateJobRequest { Id = jobId, Spec = spec };
        return ManageAsync(ManageCalls.UpdateJob, request, Job.Parser, o => _agent.UpdateJobAsync(request, o), ct);
    }

    public Task DeleteJobAsync(string jobId, CancellationToken ct)
    {
        var request = new DeleteJobRequest { Id = jobId };
        return ManageAsync(ManageCalls.DeleteJob, request, DeleteJobResponse.Parser, o => _agent.DeleteJobAsync(request, o), ct);
    }

    public Task<Run> RunJobNowAsync(string jobId, CancellationToken ct) =>
        CallAsync(o => _agent.RunJobNowAsync(new RunJobNowRequest { JobId = jobId }, o), ct);

    public async Task<IReadOnlyList<Run>> ListRunsAsync(string? jobId, int limit, CancellationToken ct) =>
        (await CallAsync(o => _agent.ListRunsAsync(new ListRunsRequest { JobId = jobId ?? "", Limit = limit }, o), ct)).Runs;

    public async IAsyncEnumerable<RunProgress> WatchRunAsync(string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _agent.WatchRun(new WatchRunRequest { RunId = runId }, cancellationToken: ct);
        while (true)
        {
            bool more;
            try
            {
                more = await call.ResponseStream.MoveNext(ct);
            }
            catch (RpcException ex)
            {
                throw Translate(ex, ct);
            }

            if (!more)
            {
                yield break;
            }

            yield return call.ResponseStream.Current;
        }
    }

    public Task CancelRunAsync(string runId, CancellationToken ct) =>
        CallAsync(o => _agent.CancelRunAsync(new CancelRunRequest { RunId = runId }, o), ct);

    public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string jobId, CancellationToken ct) =>
        (await CallAsync(o => _agent.ListSnapshotsAsync(new ListSnapshotsRequest { JobId = jobId }, o), ct)).Snapshots;

    public async Task<IReadOnlyList<SnapshotEntry>> BrowseSnapshotAsync(string jobId, string snapshotId, string path, CancellationToken ct) =>
        (await CallAsync(o => _agent.BrowseSnapshotAsync(new BrowseSnapshotRequest { JobId = jobId, SnapshotId = snapshotId, Path = path }, o), ct)).Entries;

    public Task<Run> StartRestoreAsync(StartRestoreRequest request, CancellationToken ct) =>
        ManageAsync(ManageCalls.StartRestore, request, Run.Parser, o => _agent.StartRestoreAsync(request, o), ct);

    public void Dispose() => _channel.Dispose();

    private async Task<T> ManageAsync<T>(string method, IMessage request, MessageParser<T> parser, Func<CallOptions, AsyncUnaryCall<T>> direct, CancellationToken ct)
        where T : class, IMessage<T>
    {
        var status = await GetStatusAsync(ct);
        return status.CallerCanManage
            ? await CallAsync(direct, ct)
            : await _elevated.InvokeAsync(method, request, parser, ct);
    }

    private static async Task<T> CallAsync<T>(Func<CallOptions, AsyncUnaryCall<T>> start, CancellationToken ct)
    {
        try
        {
            using var call = start(new CallOptions(cancellationToken: ct));
            return await call.ResponseAsync;
        }
        catch (RpcException ex)
        {
            throw Translate(ex, ct);
        }
    }

    private static Exception Translate(RpcException ex, CancellationToken ct) =>
        ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested
            ? new OperationCanceledException("The call was canceled.", ex, ct)
            : AgentCallException.FromRpc(ex);
}
