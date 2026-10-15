using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Agent.Scheduling;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Google.Protobuf.WellKnownTypes;
using Proto = CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Agent.Api;

/// <summary>Between the gRPC contract and the agent's own types. Validation beyond shape happens in the services.</summary>
internal static partial class ProtoMapping
{
    public static Proto.Destination ToProto(DestinationRecord destination, DestinationStatus status) => new()
    {
        Id = destination.Id.ToString(),
        Name = destination.Name,
        Kind = destination.Kind == DestinationKind.FileSystem ? Proto.DestinationKind.FileSystem : Proto.DestinationKind.Unspecified,
        Location = destination.Location,
        State = status.State switch
        {
            DestinationConnection.Connected => Proto.DestinationState.Connected,
            DestinationConnection.Unavailable => Proto.DestinationState.Unavailable,
            _ => Proto.DestinationState.Connecting,
        },
        StateMessage = status.Message ?? "",
        CreatedAt = Timestamp.FromDateTimeOffset(destination.CreatedAt),
    };

    public static DestinationKind FromProto(Proto.DestinationKind kind) => kind switch
    {
        Proto.DestinationKind.FileSystem => DestinationKind.FileSystem,
        _ => throw new AgentException(AgentError.InvalidArgument, "Choose a destination kind."),
    };

    public static Proto.Job ToProto(JobView view)
    {
        var job = view.Job;
        var schedule = ScheduleSpec.From(job);
        var proto = new Proto.Job
        {
            Id = job.Id.ToString(),
            Spec = new Proto.JobSpec
            {
                Name = job.Name,
                DestinationId = job.DestinationId.ToString(),
                SourcePath = job.SourcePath,
                Retention = new Proto.Retention
                {
                    KeepLatest = job.KeepLatest,
                    KeepHourly = job.KeepHourly,
                    KeepDaily = job.KeepDaily,
                    KeepWeekly = job.KeepWeekly,
                    KeepMonthly = job.KeepMonthly,
                    KeepAnnual = job.KeepAnnual,
                },
                Excludes = { job.Excludes },
                Compression = job.Compression switch
                {
                    BackupCompression.Off => Proto.Compression.Off,
                    BackupCompression.Fastest => Proto.Compression.Fastest,
                    BackupCompression.Smallest => Proto.Compression.Smallest,
                    _ => Proto.Compression.Balanced,
                },
                Schedule = schedule.Kind switch
                {
                    ScheduleKind.Daily => new Proto.Schedule { Daily = new Proto.DailySchedule { MinuteOfDay = schedule.MinuteOfDay, DaysOfWeek = { schedule.DaysOfWeek.Select(d => (int)d) } } },
                    ScheduleKind.Interval => new Proto.Schedule { Interval = new Proto.IntervalSchedule { Hours = schedule.IntervalHours } },
                    _ => new Proto.Schedule { Manual = new Proto.ManualSchedule() },
                },
                Enabled = job.Enabled,
            },
            CreatedAt = Timestamp.FromDateTimeOffset(job.CreatedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(job.UpdatedAt),
        };

        if (view.LastRun is { } last)
        {
            proto.LastRun = ToProto(last);
        }

        if (view.NextRunAt is { } next)
        {
            proto.NextRunAt = Timestamp.FromDateTimeOffset(next);
        }

        return proto;
    }

    public static JobInput FromProto(Proto.JobSpec? spec)
    {
        if (spec is null)
        {
            throw new AgentException(AgentError.InvalidArgument, "The job spec is missing.");
        }

        var retention = spec.Retention is { } r
            ? new RetentionRule { KeepLatest = r.KeepLatest, KeepHourly = r.KeepHourly, KeepDaily = r.KeepDaily, KeepWeekly = r.KeepWeekly, KeepMonthly = r.KeepMonthly, KeepAnnual = r.KeepAnnual }
            : RetentionRule.Default;
        var compression = spec.Compression switch
        {
            Proto.Compression.Off => BackupCompression.Off,
            Proto.Compression.Fastest => BackupCompression.Fastest,
            Proto.Compression.Smallest => BackupCompression.Smallest,
            _ => BackupCompression.Balanced,
        };
        var schedule = spec.Schedule?.KindCase switch
        {
            Proto.Schedule.KindOneofCase.Daily => new ScheduleSpec(ScheduleKind.Daily, spec.Schedule.Daily.MinuteOfDay, DayBits(spec.Schedule.Daily.DaysOfWeek), 0),
            Proto.Schedule.KindOneofCase.Interval => ScheduleSpec.EveryHours(spec.Schedule.Interval.Hours),
            _ => ScheduleSpec.Manual,
        };

        return new JobInput(spec.Name, AgentException.ParseId(spec.DestinationId, "destination"), spec.SourcePath, retention, [.. spec.Excludes], compression, schedule, spec.Enabled);
    }

    public static Proto.Run ToProto(RunRecord run)
    {
        var proto = new Proto.Run
        {
            Id = run.Id.ToString(),
            JobId = run.JobId?.ToString() ?? "",
            Kind = run.Kind switch { RunKind.Restore => Proto.RunKind.Restore, RunKind.Verify => Proto.RunKind.Verify, _ => Proto.RunKind.Backup },
            State = ToProto(run.State),
            Trigger = run.Trigger switch
            {
                RunTrigger.Scheduled => Proto.RunTrigger.Scheduled,
                RunTrigger.CatchUp => Proto.RunTrigger.CatchUp,
                _ => Proto.RunTrigger.Manual,
            },
            StartedAt = Timestamp.FromDateTimeOffset(run.StartedAt),
            SnapshotId = run.SnapshotId ?? "",
            Files = run.Files,
            Bytes = run.Bytes,
            FailedItemCount = run.FailedItemCount,
            FailedItems = { run.FailedItems.Select(f => new Proto.FailedItem { Path = f.Path, Error = f.Error }) },
            Message = run.Message ?? "",
            RestoreTarget = run.RestoreTarget ?? "",
        };

        if (run.FinishedAt is { } finished)
        {
            proto.FinishedAt = Timestamp.FromDateTimeOffset(finished);
        }

        return proto;
    }

    public static Proto.RunState ToProto(RunState state) => state switch
    {
        RunState.Running => Proto.RunState.Running,
        RunState.Succeeded => Proto.RunState.Succeeded,
        RunState.CompletedWithWarnings => Proto.RunState.CompletedWithWarnings,
        RunState.Failed => Proto.RunState.Failed,
        RunState.Canceled => Proto.RunState.Canceled,
        _ => Proto.RunState.Unspecified,
    };

    public static Proto.RunProgress ToProto(Guid runId, RunProgress? progress, RunRecord? finished)
    {
        var proto = new Proto.RunProgress
        {
            RunId = runId.ToString(),
            State = ToProto(finished?.State ?? progress?.State ?? RunState.Running),
            FilesProcessed = progress?.FilesProcessed ?? finished?.Files ?? 0,
            BytesProcessed = progress?.BytesProcessed ?? finished?.Bytes ?? 0,
            FilesHashed = progress?.FilesHashed ?? 0,
            BytesHashed = progress?.BytesHashed ?? 0,
            EstimatedFiles = progress?.EstimatedFiles ?? 0,
            EstimatedBytes = progress?.EstimatedBytes ?? 0,
            ErrorCount = progress?.ErrorCount ?? finished?.FailedItemCount ?? 0,
        };

        if (finished is not null)
        {
            proto.Finished = ToProto(finished);
        }

        return proto;
    }

    public static Proto.Snapshot ToProto(SnapshotInfo snapshot, Guid jobId) => new()
    {
        Id = snapshot.Id,
        JobId = jobId.ToString(),
        SourcePath = snapshot.SourcePath,
        StartTime = Timestamp.FromDateTimeOffset(snapshot.StartTime),
        EndTime = Timestamp.FromDateTimeOffset(snapshot.EndTime),
        Files = snapshot.Files,
        Directories = snapshot.Directories,
        Bytes = snapshot.Bytes,
        FailedItemCount = snapshot.FailedItemCount,
        Complete = snapshot.IsComplete,
        IncompleteReason = snapshot.IncompleteReason ?? "",
    };

    public static Proto.SnapshotEntry ToProto(SnapshotEntry entry)
    {
        var proto = new Proto.SnapshotEntry
        {
            Name = entry.Name,
            Path = entry.Path,
            Kind = entry.Kind switch
            {
                SnapshotEntryKind.File => Proto.EntryKind.File,
                SnapshotEntryKind.Directory => Proto.EntryKind.Directory,
                SnapshotEntryKind.Symlink => Proto.EntryKind.Symlink,
                _ => Proto.EntryKind.Other,
            },
            Size = entry.Size,
        };

        if (entry.ModifiedAt is { } modified)
        {
            proto.ModifiedAt = Timestamp.FromDateTimeOffset(modified);
        }

        return proto;
    }

    private static int DayBits(IEnumerable<int> days)
    {
        var bits = 0;
        foreach (var day in days)
        {
            if (day is < 0 or > 6)
            {
                throw new AgentException(AgentError.InvalidArgument, "Days of the week must be 0 (Sunday) to 6 (Saturday).");
            }

            bits |= 1 << day;
        }

        return bits;
    }
}
