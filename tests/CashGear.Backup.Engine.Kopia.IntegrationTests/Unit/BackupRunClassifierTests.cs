using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Runs;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Unit;

/// <summary>CLAUDE.md rule 5 without Kopia: every way a run can end, and which ones count as a restore point.</summary>
public class BackupRunClassifierTests
{
    private static TaskInfo Task(string status, long errors = 0, string? message = null) => new()
    {
        Id = "7",
        Kind = TaskInfo.KindSnapshot,
        Status = status,
        ErrorMessage = message,
        Counters = new() { ["Errors"] = new TaskCounter(errors, null, "error") },
    };

    private static SnapshotItem Snapshot(string id = "s1", string? incomplete = null, int failed = 0, int ignored = 0, bool summary = true) => new()
    {
        Id = id,
        RootId = "k0123456789abcdef0123456789abcdef",
        IncompleteReason = incomplete,
        Summary = summary ? new SnapshotSummary { Files = 3, Size = 10, NumFailed = failed, NumIgnoredErrors = ignored } : null,
    };

    [Fact]
    public void Success_with_one_clean_complete_snapshot_is_Succeeded()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [Snapshot()], cancelRequested: false);
        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("s1", result.Snapshot?.Id);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Failed_or_skipped_files_make_it_CompletedWithWarnings(int failed, int ignored)
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess, errors: failed), [Snapshot(failed: failed, ignored: ignored)], cancelRequested: false);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
        Assert.Equal("s1", result.Snapshot?.Id);
    }

    [Fact]
    public void Task_errors_without_a_failed_summary_still_warn()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess, errors: 2), [Snapshot()], cancelRequested: false);
        Assert.Equal(RunState.CompletedWithWarnings, result.State);
    }

    [Fact]
    public void A_canceled_incomplete_snapshot_is_Canceled_even_though_Kopia_says_SUCCESS()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [Snapshot(incomplete: SnapshotItem.IncompleteCanceled)], cancelRequested: true);
        Assert.Equal(RunState.Canceled, result.State);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Any_other_incomplete_snapshot_is_Failed()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [Snapshot(incomplete: "limit-reached")], cancelRequested: false);
        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.Snapshot);
        Assert.Contains("limit-reached", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Checkpoints_left_by_a_long_run_are_not_the_result()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess),
            [Snapshot("cp", incomplete: SnapshotItem.IncompleteCheckpoint), Snapshot("final")], cancelRequested: false);
        Assert.Equal(RunState.Succeeded, result.State);
        Assert.Equal("final", result.Snapshot?.Id);
    }

    [Fact]
    public void Success_without_a_new_snapshot_is_Failed()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [], cancelRequested: false);
        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void No_new_snapshot_after_a_cancel_request_is_Canceled()
    {
        Assert.Equal(RunState.Canceled, BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [], cancelRequested: true).State);
    }

    [Fact]
    public void More_than_one_new_snapshot_is_Failed()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [Snapshot("a"), Snapshot("b")], cancelRequested: false);
        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void A_snapshot_without_a_summary_is_Failed()
    {
        Assert.Equal(RunState.Failed, BackupRunClassifier.Classify(Task(TaskInfo.StatusSuccess), [Snapshot(summary: false)], cancelRequested: false).State);
    }

    [Fact]
    public void A_failed_task_is_Failed_with_its_message_even_if_a_snapshot_appeared()
    {
        var result = BackupRunClassifier.Classify(Task(TaskInfo.StatusFailed, message: "upload error: disk full"), [Snapshot()], cancelRequested: false);
        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.Snapshot);
        Assert.Contains("disk full", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_canceled_task_is_Canceled()
    {
        Assert.Equal(RunState.Canceled, BackupRunClassifier.Classify(Task(TaskInfo.StatusCanceled), [], cancelRequested: true).State);
    }

    [Fact]
    public void A_task_that_was_never_found_is_Failed()
    {
        var result = BackupRunClassifier.Classify(null, [Snapshot()], cancelRequested: false);
        Assert.Equal(RunState.Failed, result.State);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void A_task_that_has_not_finished_is_Failed()
    {
        Assert.Equal(RunState.Failed, BackupRunClassifier.Classify(Task("RUNNING"), [Snapshot()], cancelRequested: false).State);
    }
}
