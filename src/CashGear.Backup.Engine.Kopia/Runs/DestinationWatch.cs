using System.Globalization;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;

namespace CashGear.Backup.Engine.Kopia.Runs;

/// <summary>What the destination did when it was lost, from the words of Kopia's storage errors.</summary>
internal enum DestinationProblem
{
    Unreachable,
    AccessDenied,
    Full,
}

/// <summary>A storage call in Kopia's task log: a write it will try again, or one it gave up on after its retries.</summary>
internal enum StorageError
{
    Retried,
    GaveUp,
}

/// <summary>A backup's destination has been failing for <see cref="FailingFor"/>; <see cref="LastError"/> is Kopia's latest error, for the log only.</summary>
internal sealed record DestinationLoss(DestinationProblem Problem, TimeSpan FailingFor, string LastError)
{
    /// <summary>For the user: what happened and what to do. Never quotes Kopia's error (blob names, addresses).</summary>
    public string Message
    {
        get
        {
            var seconds = ((int)FailingFor.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return Problem switch
            {
                DestinationProblem.Full =>
                    $"Lost the destination: it is full and refused every write for {seconds} s, so the backup was stopped. No restore point was recorded; free some space on it and run the backup again.",
                DestinationProblem.AccessDenied =>
                    $"Lost the destination: it refused every write for {seconds} s (access denied), so the backup was stopped. No restore point was recorded; check the destination's permissions and run the backup again.",
                _ =>
                    $"Lost the destination: it could not be reached for {seconds} s, so the backup was stopped. No restore point was recorded; the next run starts again once the destination is back.",
            };
        }
    }
}

/// <summary>
/// Decides when a backup has lost its destination (docs/hardening.md, finding 3; CLAUDE.md rule 10: this decides how
/// runs end). Kopia does not fail a snapshot when the destination goes away: it retries each write about 10 times
/// (some 11 s), fails the files in that pack and goes on with the next ones, so a large backup to an unplugged NAS
/// would show "running" for hours. Its task log records every retry (<c>... when PutBlob(...) (#N), sleeping for ...
/// before retrying</c>) and every write it gives up on (<c>... unable to complete PutBlob(...) despite 10 retries</c>;
/// a folder destination gives up after 2 retries, <c>PutBlobInPath:...</c>, and logs no retries). The log keeps the
/// last 1,000 lines only, so a burst can push some out between reads; an outage keeps adding new ones.
/// <para>
/// The destination is lost when Kopia has given up at least one write (so the run can no longer record a complete
/// restore point) and storage errors have gone on for <c>window</c> without a pause of <c>quietPeriod</c>. A pause that
/// long means writes go through again: the next error starts the count afresh. The decision is taken when an error
/// arrives, never from silence, and it uses the log's own timestamps, so polling delays do not stretch an outage.
/// </para>
/// </summary>
internal sealed class DestinationWatch(TimeSpan window, TimeSpan quietPeriod)
{
    private static readonly string[] s_full =
    [
        "space limit", "no space left", "not enough space", "disk full", "disk is full", "quota", "insufficient storage",
        "storage full", "storagefull", "storage cap",
    ];

    private static readonly string[] s_storageCall = ["Blob(", "Blobs(", "BlobInPath:", "BlobFromPath:"];

    private double _lastSeen = double.MinValue;
    private double? _failingSince;
    private double _lastFailure;
    private bool _gaveUp;
    private DestinationProblem _problem;
    private DestinationLoss? _loss;

    /// <summary>
    /// Reads the task's log (all of it, as Kopia returns it; lines already read are skipped) and returns the loss once
    /// decided. After that it keeps returning the same answer.
    /// </summary>
    public DestinationLoss? Observe(IEnumerable<TaskLogEntry> log)
    {
        foreach (var entry in log.Where(e => e.Ts > _lastSeen).OrderBy(e => e.Ts))
        {
            _lastSeen = entry.Ts;
            if (_loss is null && Classify(entry.Msg) is { } error)
            {
                Add(entry, error);
            }
        }

        return _loss;
    }

    /// <summary>Whether a log line is a storage call Kopia retried or gave up on; null for anything else.</summary>
    internal static StorageError? Classify(string? message)
    {
        // Only storage calls count, never errors reading the source. Kopia names them PutBlob(id), GetBlob(id),
        // ListBlobs(prefix) for SFTP and S3, and PutBlobInPath:<file> / GetBlobFromPath:<file> for a folder.
        if (message is null || !s_storageCall.Any(c => message.Contains(c, StringComparison.Ordinal)))
        {
            return null;
        }

        if (message.Contains("despite", StringComparison.Ordinal) && message.Contains("retries", StringComparison.Ordinal))
        {
            return StorageError.GaveUp;
        }

        return message.Contains("before retrying", StringComparison.Ordinal) ? StorageError.Retried : null;
    }

    internal static DestinationProblem ProblemOf(string message) =>
        s_full.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase)) ? DestinationProblem.Full
        : message.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
          || KopiaBackupEngine.StorageFailure(message) == BackupErrorKind.DestinationAccessDenied ? DestinationProblem.AccessDenied
        : DestinationProblem.Unreachable;

    private void Add(TaskLogEntry entry, StorageError error)
    {
        if (_failingSince is null || entry.Ts - _lastFailure > quietPeriod.TotalSeconds)
        {
            _failingSince = entry.Ts;
            _gaveUp = false;
            _problem = DestinationProblem.Unreachable;
        }

        _lastFailure = entry.Ts;
        _gaveUp |= error == StorageError.GaveUp;

        // A full destination often ends in a vaguer error ("permission denied" creating the next file): the most
        // telling word seen during the outage wins.
        var problem = ProblemOf(entry.Msg!);
        if (problem > _problem)
        {
            _problem = problem;
        }

        var failingFor = TimeSpan.FromSeconds(entry.Ts - _failingSince.Value);
        if (_gaveUp && failingFor >= window)
        {
            _loss = new DestinationLoss(_problem, failingFor, entry.Msg!);
        }
    }
}
