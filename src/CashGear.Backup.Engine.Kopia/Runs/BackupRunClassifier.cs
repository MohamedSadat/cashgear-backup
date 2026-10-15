using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;

namespace CashGear.Backup.Engine.Kopia.Runs;

/// <summary>A backup's outcome. <see cref="Snapshot"/> is set only when the run produced a restore point.</summary>
internal sealed record BackupClassification(RunState State, SnapshotItem? Snapshot, string? Message);

/// <summary>
/// Decides a finished backup's outcome (CLAUDE.md rule 5). Kopia's task status is not enough on its own: it
/// reports SUCCESS when files failed and when the run was canceled.
/// </summary>
internal static class BackupRunClassifier
{
    /// <param name="task">The run's Kopia task, or null if it was never found.</param>
    /// <param name="newSnapshots">Snapshots of the source that did not exist before the run.</param>
    /// <param name="cancelRequested">Whether <c>CancelAsync</c> was called for the run.</param>
    public static BackupClassification Classify(TaskInfo? task, IReadOnlyList<SnapshotItem> newSnapshots, bool cancelRequested)
    {
        if (task is null)
        {
            return Failed("Kopia never reported a task for this backup.");
        }

        switch (task.Status)
        {
            case TaskInfo.StatusFailed:
                return Failed($"Kopia reported the backup as failed: {task.ErrorMessage}");
            case TaskInfo.StatusCanceled:
                return new(RunState.Canceled, null, "The backup was canceled.");
            case not TaskInfo.StatusSuccess:
                return Failed($"Kopia's task ended in an unexpected state '{task.Status}'.");
        }

        // Checkpoints are interim snapshots a long upload saves along the way; they are never the result.
        var candidates = newSnapshots.Where(s => s.IncompleteReason != SnapshotItem.IncompleteCheckpoint).ToList();
        if (candidates.Count == 0)
        {
            return cancelRequested
                ? new(RunState.Canceled, null, "The backup was canceled before it recorded anything.")
                : Failed("Kopia reported success but recorded no snapshot.");
        }

        if (candidates.Count > 1)
        {
            return Failed($"Kopia recorded {candidates.Count} snapshots for one run; expected exactly one ({string.Join(", ", candidates.Select(s => s.Id))}).");
        }

        var snapshot = candidates[0];
        if (snapshot.IncompleteReason == SnapshotItem.IncompleteCanceled)
        {
            return new(RunState.Canceled, null, "The backup was canceled; the partial snapshot it left is not a restore point.");
        }

        if (snapshot.IncompleteReason is not null)
        {
            return Failed($"Kopia recorded an incomplete snapshot ({snapshot.IncompleteReason}).");
        }

        if (snapshot.Summary is not { } summary)
        {
            return Failed($"Snapshot {snapshot.Id} has no summary, so its completeness cannot be checked.");
        }

        var problems = (long)summary.NumFailed + summary.NumIgnoredErrors;
        if (problems > 0 || task.Counter("Errors") > 0)
        {
            return new(RunState.CompletedWithWarnings, snapshot,
                $"{Math.Max(problems, task.Counter("Errors"))} item(s) could not be backed up.");
        }

        return new(RunState.Succeeded, snapshot, null);
    }

    private static BackupClassification Failed(string message) => new(RunState.Failed, null, message);
}
