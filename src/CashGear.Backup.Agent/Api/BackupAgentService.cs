using CashGear.Backup.Agent.Destinations;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Security;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Proto = CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Agent.Api;

/// <summary>
/// The desktop app's API. Every call checks its <see cref="AccessLevel"/> first. Request messages carrying
/// passwords are never logged.
/// </summary>
internal sealed class BackupAgentService(
    CallerAuthorization auth,
    AgentState state,
    DestinationService destinations,
    JobService jobs,
    RunCoordinator runs,
    EngineHost engines) : Proto.BackupAgent.BackupAgentBase
{
    public override Task<Proto.AgentStatus> GetStatus(Proto.GetStatusRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return Task.FromResult(new Proto.AgentStatus
        {
            AgentVersion = AgentInfo.Version,
            MachineIdentity = state.MachineIdentity,
            StartedAt = Timestamp.FromDateTimeOffset(state.StartedAt),
            Ready = state.Ready,
            CallerCanManage = auth.CanManage(auth.Identify(context)),
        });
    }

    public override async Task<Proto.ListDestinationsResponse> ListDestinations(Proto.ListDestinationsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return new Proto.ListDestinationsResponse
        {
            Destinations = { (await destinations.ListAsync(context.CancellationToken)).Select(d => ProtoMapping.ToProto(d.Destination, d.Status)) },
        };
    }

    public override async Task<Proto.Destination> CreateDestination(Proto.CreateDestinationRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var record = await destinations.AddAsync(request.Name, ProtoMapping.FromProto(request.Kind), request.Location, request.Password, create: true, context.CancellationToken);
        return ProtoMapping.ToProto(record, engines.StatusOf(record.Id));
    }

    public override async Task<Proto.Destination> ConnectDestination(Proto.ConnectDestinationRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var record = await destinations.AddAsync(request.Name, ProtoMapping.FromProto(request.Kind), request.Location, request.Password, create: false, context.CancellationToken);
        return ProtoMapping.ToProto(record, engines.StatusOf(record.Id));
    }

    public override async Task<Proto.ListJobsResponse> ListJobs(Proto.ListJobsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return new Proto.ListJobsResponse { Jobs = { (await jobs.ListAsync(context.CancellationToken)).Select(ProtoMapping.ToProto) } };
    }

    public override async Task<Proto.Job> CreateJob(Proto.CreateJobRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        return ProtoMapping.ToProto(await jobs.CreateAsync(ProtoMapping.FromProto(request.Spec), context.CancellationToken));
    }

    public override async Task<Proto.Job> UpdateJob(Proto.UpdateJobRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var id = AgentException.ParseId(request.Id, "job");
        return ProtoMapping.ToProto(await jobs.UpdateAsync(id, ProtoMapping.FromProto(request.Spec), context.CancellationToken));
    }

    public override async Task<Proto.DeleteJobResponse> DeleteJob(Proto.DeleteJobRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        await jobs.DeleteAsync(AgentException.ParseId(request.Id, "job"), context.CancellationToken);
        return new Proto.DeleteJobResponse();
    }

    public override async Task<Proto.Run> RunJobNow(Proto.RunJobNowRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Operate);
        var run = await runs.StartBackupAsync(AgentException.ParseId(request.JobId, "job"), Data.RunTrigger.Manual, context.CancellationToken);
        return ProtoMapping.ToProto(run);
    }

    public override async Task<Proto.ListRunsResponse> ListRuns(Proto.ListRunsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        Guid? jobId = string.IsNullOrEmpty(request.JobId) ? null : AgentException.ParseId(request.JobId, "job");
        return new Proto.ListRunsResponse { Runs = { (await runs.ListAsync(jobId, request.Limit, context.CancellationToken)).Select(ProtoMapping.ToProto) } };
    }

    public override async Task WatchRun(Proto.WatchRunRequest request, IServerStreamWriter<Proto.RunProgress> responseStream, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        await foreach (var update in runs.WatchAsync(AgentException.ParseId(request.RunId, "run"), context.CancellationToken))
        {
            await responseStream.WriteAsync(ProtoMapping.ToProto(update.RunId, update.Progress, update.Finished), context.CancellationToken);
        }
    }

    public override async Task<Proto.CancelRunResponse> CancelRun(Proto.CancelRunRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Operate);
        await runs.CancelAsync(AgentException.ParseId(request.RunId, "run"), context.CancellationToken);
        return new Proto.CancelRunResponse();
    }

    public override async Task<Proto.ListSnapshotsResponse> ListSnapshots(Proto.ListSnapshotsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        var jobId = AgentException.ParseId(request.JobId, "job");
        var (job, destination) = await runs.LoadJobAsync(jobId, context.CancellationToken);
        var engine = await engines.ConnectAsync(destination, context.CancellationToken);
        var snapshots = await engine.ListSnapshotsAsync(JobMapping.ToEngineJob(job), context.CancellationToken);
        return new Proto.ListSnapshotsResponse { Snapshots = { snapshots.Select(s => ProtoMapping.ToProto(s, jobId)) } };
    }

    public override async Task<Proto.BrowseSnapshotResponse> BrowseSnapshot(Proto.BrowseSnapshotRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        var (job, destination) = await runs.LoadJobAsync(AgentException.ParseId(request.JobId, "job"), context.CancellationToken);
        var engine = await engines.ConnectAsync(destination, context.CancellationToken);
        var snapshot = (await engine.ListSnapshotsAsync(JobMapping.ToEngineJob(job), context.CancellationToken)).FirstOrDefault(s => s.Id == request.SnapshotId)
            ?? throw new AgentException(AgentError.NotFound, $"Snapshot {request.SnapshotId} is not a snapshot of this job.");
        var entries = await engine.BrowseAsync(snapshot, request.Path, context.CancellationToken);
        return new Proto.BrowseSnapshotResponse { Entries = { entries.Select(ProtoMapping.ToProto) } };
    }

    public override async Task<Proto.Run> StartRestore(Proto.StartRestoreRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var run = await runs.StartRestoreAsync(AgentException.ParseId(request.JobId, "job"), request.SnapshotId, request.Path, request.TargetFolder, context.CancellationToken);
        return ProtoMapping.ToProto(run);
    }
}
