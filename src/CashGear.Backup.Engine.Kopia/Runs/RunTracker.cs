using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;

namespace CashGear.Backup.Engine.Kopia.Runs;

/// <summary>
/// The state of one backup or restore: the latest progress, and a signal that fires on every update so any
/// number of watchers can follow it. Once a result is published nothing changes any more.
/// </summary>
internal sealed class RunTracker
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = NewSignal();
    private RunProgress _latest;

    public RunTracker(RunHandle handle, Guid repositoryId, SourceInfo? source)
    {
        Handle = handle;
        RepositoryId = repositoryId;
        Source = source;
        _latest = new RunProgress { Run = handle, State = RunState.Running };
    }

    public RunHandle Handle { get; }
    public Guid RepositoryId { get; }

    /// <summary>Backup only: the Kopia source being snapshotted.</summary>
    public SourceInfo? Source { get; }

    /// <summary>The Kopia task, once known (restore: at once; backup: when the monitor finds it).</summary>
    public string? TaskId { get; set; }

    public bool CancelRequested { get; set; }

    public Task Monitor { get; set; } = Task.CompletedTask;

    public bool IsFinished
    {
        get
        {
            lock (_lock)
            {
                return _latest.Result is not null;
            }
        }
    }

    public (RunProgress Latest, Task Changed) Current()
    {
        lock (_lock)
        {
            return (_latest, _changed.Task);
        }
    }

    /// <summary>Publishes an update; ignored once the run has its result.</summary>
    public void Publish(Func<RunProgress, RunProgress> update)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_latest.Result is not null)
            {
                return;
            }

            _latest = update(_latest);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    public void Finish(RunResult result) =>
        Publish(p => p with { State = result.State, Result = result });

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
