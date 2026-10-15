using CashGear.Backup.Agent.Destinations;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
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
    EngineHost engines,
    SettingsStore settings,
    AlertService alerts,
    IEmailSender email,
    Licensing.LicenceService licences) : Proto.BackupAgent.BackupAgentBase
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
        var input = ProtoMapping.FromProto(request.Name, request.Kind, request.Location, request.Share, request.Sftp, request.S3);
        DestinationRecord record;
        using (input.Credential)
        {
            record = await destinations.AddAsync(input, request.Password, create: true, context.CancellationToken);
        }

        return ProtoMapping.ToProto(record, engines.StatusOf(record.Id));
    }

    public override async Task<Proto.Destination> ConnectDestination(Proto.ConnectDestinationRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var input = ProtoMapping.FromProto(request.Name, request.Kind, request.Location, request.Share, request.Sftp, request.S3);
        DestinationRecord record;
        using (input.Credential)
        {
            record = await destinations.AddAsync(input, request.Password, create: false, context.CancellationToken);
        }

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
        var (engine, snapshot) = await SnapshotAsync(request.JobId, request.SnapshotId, context.CancellationToken);
        var entries = await engine.BrowseAsync(snapshot, request.Path, context.CancellationToken);
        return new Proto.BrowseSnapshotResponse { Entries = { entries.Select(ProtoMapping.ToProto) } };
    }

    public override async Task SearchSnapshot(Proto.SearchSnapshotRequest request, IServerStreamWriter<Proto.SearchSnapshotResponse> responseStream, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        var query = request.Query.Trim();
        if (query.Length is < 2 or > 200)
        {
            throw new AgentException(AgentError.InvalidArgument, "Search for 2 to 200 characters of a name.");
        }

        var maxResults = request.MaxResults <= 0 ? 200 : Math.Min(request.MaxResults, 1000);
        var (engine, snapshot) = await SnapshotAsync(request.JobId, request.SnapshotId, context.CancellationToken);
        await foreach (var batch in SnapshotSearch.SearchAsync(engine, snapshot, query, maxResults, context.CancellationToken))
        {
            await responseStream.WriteAsync(new Proto.SearchSnapshotResponse
            {
                Matches = { batch.Matches.Select(ProtoMapping.ToProto) },
                Done = batch.Done,
                Truncated = batch.Truncated,
                FoldersSearched = batch.FoldersSearched,
            }, context.CancellationToken);
        }
    }

    public override async Task<Proto.Run> StartRestore(Proto.StartRestoreRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var run = await runs.StartRestoreAsync(AgentException.ParseId(request.JobId, "job"), request.SnapshotId, request.Path, request.TargetFolder,
            toOriginal: request.Location == Proto.RestoreLocation.Original, context.CancellationToken);
        return ProtoMapping.ToProto(run);
    }

    public override async Task<Proto.Run> VerifyJobNow(Proto.VerifyJobNowRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Operate);
        var current = await settings.LoadAsync(context.CancellationToken);
        var run = await runs.StartVerifyAsync(AgentException.ParseId(request.JobId, "job"), Data.RunTrigger.Manual, current.VerifySamplePercent / 100, context.CancellationToken);
        return ProtoMapping.ToProto(run);
    }

    public override async Task<Proto.ListAlertsResponse> ListAlerts(Proto.ListAlertsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return new Proto.ListAlertsResponse { Alerts = { (await alerts.ListAsync(context.CancellationToken)).Select(ProtoMapping.ToProto) } };
    }

    public override async Task<Proto.Settings> GetSettings(Proto.GetSettingsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return ProtoMapping.ToProto(await settings.LoadAsync(context.CancellationToken));
    }

    public override async Task<Proto.Settings> UpdateSettings(Proto.UpdateSettingsRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var update = ProtoMapping.FromProto(request.Settings);
        using (update.NewPassword)
        {
            if (update.Settings.Email.Enabled)
            {
                await licences.RequireAsync(l => l.EmailAlerts, "Email alerts", context.CancellationToken);
            }

            return ProtoMapping.ToProto(await settings.SaveAsync(update, context.CancellationToken));
        }
    }

    /// <summary>Manage: it makes the agent (LocalSystem) connect to a server the caller names.</summary>
    public override async Task<Proto.SendTestEmailResponse> SendTestEmail(Proto.SendTestEmailRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        var (config, given) = ProtoMapping.FromProto(request.Email);
        using (given)
        {
            var saved = await settings.LoadAsync(context.CancellationToken);
            using var stored = given is null && config.Username.Length > 0 ? settings.EmailPassword(saved) : null;
            var errors = SettingsStore.ValidateEmail(config with { ProtectedPassword = null }, requireComplete: true);
            if (config.Security == SmtpSecurityMode.None && (given ?? stored) is not null && errors.Count == 0)
            {
                errors.Add("Without encryption the email password would cross the network readable: choose STARTTLS or TLS.");
            }

            if (errors.Count > 0)
            {
                throw new AgentException(AgentError.InvalidArgument, string.Join(" ", errors));
            }

            var (subject, body) = Housekeeping.TestEmail();
            await email.SendAsync(config, given ?? stored, subject, body, context.CancellationToken);
        }

        return new Proto.SendTestEmailResponse();
    }

    public override async Task<Proto.LicenceStatus> GetLicence(Proto.GetLicenceRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Read);
        return ProtoMapping.ToProto(await licences.GetAsync(context.CancellationToken));
    }

    public override async Task<Proto.LicenceStatus> ActivateLicence(Proto.ActivateLicenceRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        return ProtoMapping.ToProto(await licences.ActivateAsync(request.Key, context.CancellationToken));
    }

    public override async Task<Proto.LicenceStatus> RemoveLicence(Proto.RemoveLicenceRequest request, ServerCallContext context)
    {
        auth.Require(context, AccessLevel.Manage);
        return ProtoMapping.ToProto(await licences.RemoveAsync(context.CancellationToken));
    }

    private async Task<(IBackupEngine Engine, SnapshotInfo Snapshot)> SnapshotAsync(string jobId, string snapshotId, CancellationToken ct)
    {
        var (job, destination) = await runs.LoadJobAsync(AgentException.ParseId(jobId, "job"), ct);
        var engine = await engines.ConnectAsync(destination, ct);
        var snapshot = (await engine.ListSnapshotsAsync(JobMapping.ToEngineJob(job), ct)).FirstOrDefault(s => s.Id == snapshotId)
            ?? throw new AgentException(AgentError.NotFound, $"Snapshot {snapshotId} is not a snapshot of this job.");
        return (engine, snapshot);
    }
}
