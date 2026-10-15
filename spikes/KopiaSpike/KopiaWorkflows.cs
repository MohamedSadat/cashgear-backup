using System.Diagnostics;
using System.Text.Json;

namespace KopiaSpike;

public sealed record BackupOutcome(
    JsonElement UploadResponse,
    TaskInfo? Task,
    IReadOnlyList<string> NewSnapshotIds,
    SnapshotItem? Snapshot,
    IReadOnlyList<string> SourceStatusTimeline,
    TimeSpan Elapsed)
{
    /// <summary>The product's rule for "Succeeded": exactly one new snapshot, nothing failed, not incomplete.</summary>
    public bool Succeeded =>
        Task?.Status == "SUCCESS"
        && NewSnapshotIds.Count == 1
        && Snapshot is { IncompleteReason: null, Summary.NumFailed: 0 };
}

/// <summary>Backup and restore flows on top of the raw API, including how we find the run and its snapshot.</summary>
public sealed class KopiaWorkflows(KopiaApiClient api)
{
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// POST /sources/upload returns neither a task ID nor a snapshot ID. Find the run as the new
    /// "Snapshot" task, and the snapshot as the one ID that appears in /snapshots?all=1 afterwards.
    /// </summary>
    public async Task<BackupOutcome> BackupAsync(SourceInfo source, TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var knownTasks = (await api.ListTasksAsync(ct)).Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var knownSnapshots = (await api.ListSnapshotsAsync(source, ct)).Snapshots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var timeline = new List<string>();

        var upload = await api.TriggerUploadAsync(source, ct);

        TaskInfo? task = null;
        while (stopwatch.Elapsed < timeout)
        {
            var status = (await api.ListSourcesAsync(ct)).Sources.FirstOrDefault(s => s.Source == source)?.Status ?? "missing";
            if (timeline.Count == 0 || timeline[^1] != status)
            {
                timeline.Add(status);
            }

            task ??= (await api.ListTasksAsync(ct)).Tasks.FirstOrDefault(t => t.Kind == "Snapshot" && !knownTasks.Contains(t.Id));
            if (task is not null)
            {
                task = await api.GetTaskAsync(task.Id, ct);
                if (task.IsFinished)
                {
                    break;
                }
            }

            await Task.Delay(s_poll, ct);
        }

        var after = (await api.ListSnapshotsAsync(source, ct)).Snapshots;
        var newIds = after.Select(s => s.Id).Where(id => !knownSnapshots.Contains(id)).ToList();
        var snapshot = newIds.Count == 1 ? after.Single(s => s.Id == newIds[0]) : null;
        return new BackupOutcome(upload, task, newIds, snapshot, timeline, stopwatch.Elapsed);
    }

    /// <summary>Restores a snapshot root into <paramref name="targetPath"/> and waits for the task.</summary>
    /// <param name="fullDepth">
    /// True sends restoreDirEntryAtDepth=int.MaxValue. False omits it, which shows Kopia's REST
    /// default (shallow placeholder files) and must never be used by the product.
    /// </param>
    public async Task<TaskInfo> RestoreAsync(string rootId, string targetPath, bool fullDepth, TimeSpan timeout, CancellationToken ct)
    {
        var request = new RestoreRequest(
            rootId,
            new FilesystemOutput(targetPath),
            new RestoreOptions(RestoreDirEntryAtDepth: fullDepth ? int.MaxValue : null));
        var task = await api.StartRestoreAsync(request, ct);
        return await WaitForTaskAsync(task.Id, timeout, ct);
    }

    public async Task<TaskInfo> WaitForTaskAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var task = await api.GetTaskAsync(id, ct);
            if (task.IsFinished || stopwatch.Elapsed > timeout)
            {
                return task;
            }

            await Task.Delay(s_poll, ct);
        }
    }
}
