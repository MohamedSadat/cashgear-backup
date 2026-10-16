using System.Diagnostics;
using System.Runtime.CompilerServices;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Policy;
using CashGear.Backup.Engine.Kopia.Runs;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia;

// Backup and restore runs: start, follow, cancel, classify.
public sealed partial class KopiaBackupEngine
{
    private const string Uploading = "UPLOADING";
    private const int MaxConsecutivePollErrors = 40;
    private static readonly TimeSpan s_cancelResendInterval = TimeSpan.FromSeconds(1);

    public async Task<RunHandle> StartBackupAsync(BackupJob job, CancellationToken ct)
    {
        ThrowIfDisposed();
        BackupEngineException.ThrowIfInvalid(job.Validate(), "Backup job");
        var session = Session(job.RepositoryId);
        var sourcePath = NormalizeSourcePath(job.SourcePath);
        if (!Directory.Exists(sourcePath))
        {
            throw new BackupEngineException(BackupErrorKind.SourceUnavailable, $"The folder to back up, {sourcePath}, does not exist or cannot be reached.");
        }

        var source = session.Source(sourcePath);
        var key = $"{session.Destination.RepositoryId:N}|{sourcePath}";
        var runId = Guid.NewGuid();
        if (!_activeSources.TryAdd(key, runId))
        {
            throw new BackupEngineException(BackupErrorKind.RunAlreadyActive, $"A backup of {sourcePath} is already running.");
        }

        try
        {
            var server = await session.Host.GetSessionAsync(ct);
            var client = server.Client;

            // Rule 8: never run under Kopia's defaults. Verify the stored policy and rewrite it if it drifted.
            var policy = KopiaPolicyMapper.ToPolicy(job);
            var drift = KopiaPolicyMapper.Differences(policy, await client.GetPolicyAsync(source, ct));
            var registered = (await client.ListSourcesAsync(ct)).Sources.Any(s => s.Source == source);
            if (drift.Count > 0 || !registered)
            {
                if (registered)
                {
                    LogPolicyDrift(_logger, job.Id, string.Join("; ", drift));
                }

                await WritePolicyAsync(session, client, job, source, ct);
            }

            // Kopia's upload call returns neither a task nor a snapshot ID: remember what exists now.
            var knownSnapshots = (await client.ListSnapshotsAsync(source, ct)).Snapshots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var knownTasks = (await client.ListTasksAsync(ct)).Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

            var upload = await client.StartUploadAsync(source, ct);
            if (!upload.Sources.TryGetValue(source.ToString(), out var accepted) || !accepted.Success)
            {
                throw Protocol($"Kopia did not accept the backup request for {source}.");
            }

            var handle = new RunHandle(runId, RunKind.Backup, session.Destination.RepositoryId, job.Id, DateTimeOffset.UtcNow);
            var tracker = new RunTracker(handle, session.Destination.RepositoryId, source);
            _runs[runId] = tracker;
            tracker.Monitor = Task.Run(() => MonitorBackupAsync(tracker, session, server.Generation, knownSnapshots, knownTasks, key), CancellationToken.None);
            LogBackupStarted(_logger, runId, job.Id, sourcePath);
            return handle;
        }
        catch (Exception ex)
        {
            _activeSources.TryRemove(KeyValuePair.Create(key, runId));
            if (IsKopiaFailure(ex))
            {
                throw Map(ex);
            }

            throw;
        }
    }

    public async Task<RunHandle> RestoreAsync(RestoreRequest request, CancellationToken ct)
    {
        ThrowIfDisposed();
        BackupEngineException.ThrowIfInvalid(request.Validate(), "Restore request");
        var session = Session(request.Snapshot.RepositoryId);
        var rootId = ValidRootId(request.Snapshot);
        var segments = SnapshotPath.Split(request.PathInSnapshot) ?? throw InvalidPath(request.PathInSnapshot);
        var target = Path.GetFullPath(request.TargetFolder);

        // Rule 4: a restore never overwrites. Kopia refuses a non-empty folder too (overwrite flags are off);
        // checking here fails fast with a clear error instead of a failed task.
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
        {
            throw new BackupEngineException(BackupErrorKind.RestoreTargetNotEmpty,
                $"{target} already exists and is not an empty folder; restores only go into a new or empty folder.");
        }

        try
        {
            var server = await session.Host.GetSessionAsync(ct);
            var client = server.Client;
            var root = rootId;
            var output = target;
            if (segments.Count > 0)
            {
                var entry = await FindEntryAsync(client, rootId, segments, request.PathInSnapshot, ct);
                root = rootId + "/" + string.Join('/', segments);
                if (entry.Type != DirectoryEntry.TypeDirectory)
                {
                    // A single file is written as TargetFolder\name.
                    output = Path.Combine(target, entry.Name);
                }
            }

            // An existing empty folder is a valid target for Kopia; creating it here also creates its parents.
            Directory.CreateDirectory(target);

            // The DTO defaults are the safe ones: overwrite flags off, restoreDirEntryAtDepth = int.MaxValue.
            var task = await client.StartRestoreAsync(new RestoreRequestDto(root, new FilesystemOutput(output), new RestoreOptions()), ct);
            var handle = new RunHandle(Guid.NewGuid(), RunKind.Restore, session.Destination.RepositoryId, null, DateTimeOffset.UtcNow);
            var tracker = new RunTracker(handle, session.Destination.RepositoryId, null) { TaskId = task.Id };
            _runs[handle.Id] = tracker;
            tracker.Monitor = Task.Run(() => MonitorRestoreAsync(tracker, session, server.Generation, target), CancellationToken.None);
            LogRestoreStarted(_logger, handle.Id, request.Snapshot.Id, request.PathInSnapshot, target);
            return handle;
        }
        catch (Exception ex) when (IsKopiaFailure(ex))
        {
            throw Map(ex);
        }
    }

    public async IAsyncEnumerable<RunProgress> WatchAsync(RunHandle run, [EnumeratorCancellation] CancellationToken ct)
    {
        var tracker = Tracker(run);
        RunProgress? last = null;
        while (true)
        {
            var (latest, changed) = tracker.Current();
            if (!ReferenceEquals(latest, last))
            {
                last = latest;
                yield return latest;
            }

            if (latest.Result is not null)
            {
                yield break;
            }

            await changed.WaitAsync(ct);
        }
    }

    public async Task CancelAsync(RunHandle run, CancellationToken ct)
    {
        var tracker = Tracker(run);
        if (tracker.IsFinished)
        {
            return;
        }

        tracker.CancelRequested = true;
        LogCancelRequested(_logger, run.Id);
        try
        {
            // The monitor repeats this until Kopia's task ends: a cancel that lands before the upload
            // has really started would otherwise be lost.
            await SendCancelAsync(tracker, await ClientAsync(Session(tracker.RepositoryId), ct), ct);
        }
        catch (Exception ex) when (IsKopiaFailure(ex))
        {
            throw Map(ex);
        }
    }

    private static Task SendCancelAsync(RunTracker tracker, KopiaApiClient client, CancellationToken ct) =>
        tracker.Handle.Kind == RunKind.Backup
            ? client.CancelUploadAsync(tracker.Source!, ct)
            : tracker.TaskId is { } id ? client.CancelTaskAsync(id, ct) : Task.CompletedTask;

    private async Task MonitorBackupAsync(RunTracker tracker, RepositorySession session, int generation,
        HashSet<string> knownSnapshots, HashSet<string> knownTasks, string sourceKey)
    {
        var stop = _disposing.Token;
        var source = tracker.Source!;
        var taskPrefix = source + " at ";
        var discovery = Stopwatch.StartNew();
        var lastCancel = Stopwatch.StartNew();
        TaskInfo? task = null;
        string? failure = null;
        var errors = 0;
        try
        {
            while (task is not { IsFinished: true } && failure is null)
            {
                await Task.Delay(_options.PollInterval, stop);
                try
                {
                    // A server that died or is being replaced took Kopia's task with it.
                    var server = session.Host.IsCurrent(generation) ? await session.Host.GetSessionAsync(stop) : null;
                    if (server?.Generation != generation)
                    {
                        failure = EngineStopped(session, tracker);
                        break;
                    }

                    var client = server.Client;
                    var status = (await client.ListSourcesAsync(stop)).Sources.FirstOrDefault(s => s.Source == source);
                    if (tracker.TaskId is null)
                    {
                        // Kopia shows the run as the source's currentTask while it uploads; a fast run can
                        // finish between polls, so also look for a new Snapshot task named after the source.
                        tracker.TaskId = status?.CurrentTask is { } running && !knownTasks.Contains(running)
                            ? running
                            : (await client.ListTasksAsync(stop)).Tasks.FirstOrDefault(t =>
                                t.Kind == TaskInfo.KindSnapshot && !knownTasks.Contains(t.Id)
                                && t.Description?.StartsWith(taskPrefix, StringComparison.Ordinal) == true)?.Id;
                        if (tracker.TaskId is null)
                        {
                            if (discovery.Elapsed > _options.TaskDiscoveryTimeout)
                            {
                                failure = $"Kopia did not start the backup within {_options.TaskDiscoveryTimeout.TotalSeconds:0} s.";
                            }

                            continue;
                        }
                    }

                    task = await client.GetTaskAsync(tracker.TaskId, stop);
                    var upload = status?.Status == Uploading ? status.Upload : null;
                    var current = task;
                    tracker.Publish(p => p with
                    {
                        // Processed = examined (cached + hashed); Hashed = new or changed data read. Never
                        // "Uploaded Bytes", which does not measure the data written (FINDINGS #5).
                        FilesProcessed = current.Counter("Processed Files"),
                        BytesProcessed = current.Counter("Processed Bytes"),
                        FilesHashed = current.Counter("Hashed Files"),
                        BytesHashed = current.Counter("Hashed Bytes"),
                        ErrorCount = current.Counter("Errors"),
                        EstimatedFiles = upload?.EstimatedFiles > 0 ? upload.EstimatedFiles : p.EstimatedFiles,
                        EstimatedBytes = upload?.EstimatedBytes > 0 ? upload.EstimatedBytes : p.EstimatedBytes,
                    });

                    if (tracker.CancelRequested && !task.IsFinished && lastCancel.Elapsed >= s_cancelResendInterval)
                    {
                        await SendCancelAsync(tracker, client, stop);
                        lastCancel.Restart();
                    }

                    errors = 0;
                }
                catch (Exception) when (!stop.IsCancellationRequested && !session.Host.IsCurrent(generation))
                {
                    // The call failed because the server died or is being replaced (its client may already be disposed).
                    failure = EngineStopped(session, tracker);
                }
                catch (Exception ex) when (IsKopiaFailure(ex) && !stop.IsCancellationRequested)
                {
                    if (++errors >= MaxConsecutivePollErrors)
                    {
                        failure = $"Lost contact with the backup engine: {ex.Message}";
                    }
                }
            }

            RunResult result;
            if (failure is not null)
            {
                result = Failed(tracker, failure);
            }
            else
            {
                var client = (await session.Host.GetSessionAsync(stop)).Client;
                var newSnapshots = (await client.ListSnapshotsAsync(source, stop)).Snapshots.Where(s => !knownSnapshots.Contains(s.Id)).ToList();
                result = ToResult(tracker, task, BackupRunClassifier.Classify(task, newSnapshots, tracker.CancelRequested));
            }

            tracker.Finish(result);
            LogBackupFinished(_logger, tracker.Handle.Id, tracker.Handle.JobId, result.State, result.SnapshotId, result.Files, result.FailedItemCount, result.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException && stop.IsCancellationRequested)
        {
            tracker.Finish(Failed(tracker, "The engine shut down during the run."));
        }
        catch (Exception) when (!stop.IsCancellationRequested && !session.Host.IsCurrent(generation))
        {
            tracker.Finish(Failed(tracker, EngineStopped(session, tracker)));
        }
        catch (Exception ex)
        {
            LogMonitorFailed(_logger, ex, tracker.Handle.Id);
            tracker.Finish(Failed(tracker, $"Following the run failed: {ex.Message}"));
        }
        finally
        {
            _activeSources.TryRemove(KeyValuePair.Create(sourceKey, tracker.Handle.Id));
            TrimFinishedRuns();
        }
    }

    private async Task MonitorRestoreAsync(RunTracker tracker, RepositorySession session, int generation, string target)
    {
        var stop = _disposing.Token;
        var lastCancel = Stopwatch.StartNew();
        TaskInfo? task = null;
        string? failure = null;
        var errors = 0;
        try
        {
            while (task is not { IsFinished: true } && failure is null)
            {
                await Task.Delay(_options.PollInterval, stop);
                try
                {
                    var server = session.Host.IsCurrent(generation) ? await session.Host.GetSessionAsync(stop) : null;
                    if (server?.Generation != generation)
                    {
                        failure = EngineStopped(session, tracker);
                        break;
                    }

                    task = await server.Client.GetTaskAsync(tracker.TaskId!, stop);
                    var current = task;
                    tracker.Publish(p => p with
                    {
                        FilesProcessed = current.Counter("Restored Files"),
                        BytesProcessed = current.Counter("Restored Bytes"),
                        ErrorCount = current.Counter("Ignored Errors"),
                    });

                    if (tracker.CancelRequested && !task.IsFinished && lastCancel.Elapsed >= s_cancelResendInterval)
                    {
                        await SendCancelAsync(tracker, server.Client, stop);
                        lastCancel.Restart();
                    }

                    errors = 0;
                }
                catch (Exception) when (!stop.IsCancellationRequested && !session.Host.IsCurrent(generation))
                {
                    failure = EngineStopped(session, tracker);
                }
                catch (Exception ex) when (IsKopiaFailure(ex) && !stop.IsCancellationRequested)
                {
                    if (++errors >= MaxConsecutivePollErrors)
                    {
                        failure = $"Lost contact with the backup engine: {ex.Message}";
                    }
                }
            }

            var result = failure is not null ? Failed(tracker, failure) : ClassifyRestore(tracker, task!, target);
            tracker.Finish(result);
            LogRestoreFinished(_logger, tracker.Handle.Id, result.State, result.Files, result.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException && stop.IsCancellationRequested)
        {
            tracker.Finish(Failed(tracker, "The engine shut down during the restore."));
        }
        catch (Exception) when (!stop.IsCancellationRequested && !session.Host.IsCurrent(generation))
        {
            tracker.Finish(Failed(tracker, EngineStopped(session, tracker)));
        }
        catch (Exception ex)
        {
            LogMonitorFailed(_logger, ex, tracker.Handle.Id);
            tracker.Finish(Failed(tracker, $"Following the restore failed: {ex.Message}"));
        }
        finally
        {
            TrimFinishedRuns();
        }
    }

    private static RunResult ClassifyRestore(RunTracker tracker, TaskInfo task, string target)
    {
        var (state, message) = task.Status switch
        {
            TaskInfo.StatusFailed => (RunState.Failed, $"Kopia reported the restore as failed: {task.ErrorMessage}"),
            TaskInfo.StatusCanceled => (RunState.Canceled, "The restore was canceled; the target folder may hold a partial copy."),
            TaskInfo.StatusSuccess when HasPlaceholders(target) =>
                (RunState.Failed, "Kopia restored placeholder entries (.kopia-entry) instead of the data."),
            TaskInfo.StatusSuccess when task.Counter("Ignored Errors") > 0 || task.Counter("Skipped Files") > 0 =>
                (RunState.CompletedWithWarnings, $"{task.Counter("Ignored Errors") + task.Counter("Skipped Files")} item(s) were not restored."),
            TaskInfo.StatusSuccess => (RunState.Succeeded, (string?)null),
            _ => (RunState.Failed, $"Kopia's task ended in an unexpected state '{task.Status}'."),
        };

        return new RunResult
        {
            State = state,
            StartedAt = tracker.Handle.StartedAt,
            FinishedAt = task.EndTime ?? DateTimeOffset.UtcNow,
            Files = task.Counter("Restored Files"),
            Bytes = task.Counter("Restored Bytes"),
            FailedItemCount = task.Counter("Ignored Errors") + task.Counter("Skipped Files"),
            Message = message,
        };
    }

    /// <summary>Rule 4 safety net: Kopia's shallow restore writes these instead of files and still reports SUCCESS.</summary>
    private static bool HasPlaceholders(string target) =>
        Directory.Exists(target)
        && Directory.EnumerateFiles(target, "*.kopia-entry", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Any();

    private static RunResult ToResult(RunTracker tracker, TaskInfo? task, BackupClassification classification)
    {
        var summary = classification.Snapshot?.Summary;
        return new RunResult
        {
            State = classification.State,
            StartedAt = tracker.Handle.StartedAt,
            FinishedAt = task?.EndTime ?? DateTimeOffset.UtcNow,
            SnapshotId = classification.Snapshot?.Id,
            Files = summary?.Files ?? task?.Counter("Processed Files") ?? 0,
            Bytes = summary?.Size ?? task?.Counter("Processed Bytes") ?? 0,
            FailedItemCount = summary is null ? 0 : Math.Max((long)summary.NumFailed + summary.NumIgnoredErrors, task?.Counter("Errors") ?? 0),
            FailedItems = summary?.Errors?.Select(e => new FailedItem(e.Path ?? "", e.Error ?? "")).ToList() ?? [],
            Message = classification.Message,
        };
    }

    private static RunResult Failed(RunTracker tracker, string message) => new()
    {
        State = RunState.Failed,
        StartedAt = tracker.Handle.StartedAt,
        FinishedAt = DateTimeOffset.UtcNow,
        Message = message,
    };

    /// <summary>The message of a run lost because its kopia server died or is being replaced (finding 2); logs it once.</summary>
    private string EngineStopped(RepositorySession session, RunTracker tracker)
    {
        var reason = session.Host.LastRestartReason ?? "it was restarted";
        LogRunLostToRestart(_logger, tracker.Handle.Id, reason);
        return tracker.Handle.Kind == RunKind.Backup
            ? $"The backup engine stopped during the run ({reason}); the run was lost and recorded no restore point. The engine restarts by itself and the next run starts again."
            : $"The backup engine stopped during the restore ({reason}); the restore did not finish and the target folder may hold a partial copy. The engine restarts by itself.";
    }


    private static async Task<DirectoryEntry> FindEntryAsync(KopiaApiClient client, string rootId, IReadOnlyList<string> segments, string path, CancellationToken ct)
    {
        var directory = await client.GetDirectoryAsync(rootId, ct);
        for (var i = 0; ; i++)
        {
            var entry = directory.Entries.FirstOrDefault(e => e.Name == segments[i])
                ?? throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"'{path}' is not in the snapshot.");
            if (i == segments.Count - 1)
            {
                return entry;
            }

            if (entry.Type != DirectoryEntry.TypeDirectory || !KopiaApiClient.IsValidObjectId(entry.ObjectId))
            {
                throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"'{path}' is not in the snapshot.");
            }

            directory = await client.GetDirectoryAsync(entry.ObjectId!, ct);
        }
    }

    private RunTracker Tracker(RunHandle run) =>
        _runs.TryGetValue(run.Id, out var tracker)
            ? tracker
            : throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"Run {run.Id} is unknown to this engine (finished runs are kept for a while only).");

    private void TrimFinishedRuns()
    {
        var finished = _runs.Values.Where(r => r.IsFinished).OrderBy(r => r.Handle.StartedAt).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - _options.FinishedRunsKept)))
        {
            _runs.TryRemove(old.Handle.Id, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Policy for job {JobId} drifted and is rewritten before the backup: {Differences}")]
    private static partial void LogPolicyDrift(ILogger logger, Guid jobId, string differences);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {RunId} of job {JobId} started for {Source}")]
    private static partial void LogBackupStarted(ILogger logger, Guid runId, Guid jobId, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup {RunId} of job {JobId} finished: {State}, snapshot {SnapshotId}, {Files} files, {FailedItems} failed. {Message}")]
    private static partial void LogBackupFinished(ILogger logger, Guid runId, Guid? jobId, RunState state, string? snapshotId, long files, long failedItems, string? message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore {RunId} of snapshot {SnapshotId} path '{Path}' into {Target} started")]
    private static partial void LogRestoreStarted(ILogger logger, Guid runId, string snapshotId, string path, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restore {RunId} finished: {State}, {Files} files. {Message}")]
    private static partial void LogRestoreFinished(ILogger logger, Guid runId, RunState state, long files, string? message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancel requested for run {RunId}")]
    private static partial void LogCancelRequested(ILogger logger, Guid runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Following run {RunId} failed")]
    private static partial void LogMonitorFailed(ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} was lost: the backup engine stopped during it ({Reason})")]
    private static partial void LogRunLostToRestart(ILogger logger, Guid runId, string reason);
}
