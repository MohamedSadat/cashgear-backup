using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;

namespace CashGear.Backup.Engine.Kopia.Runs;

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
    public static BackupClassification Classify(TaskInfo? task, IReadOnlyList<SnapshotItem> newSnapshots, bool cancelRequested) =>
        throw new NotImplementedException();
}
