using System.Diagnostics;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>Peak memory of the agent's process tree over one phase, in bytes.</summary>
internal sealed record MemoryPeaks(
    long AgentWorkingSet, long AgentPrivate, long KopiaWorkingSet, long KopiaPrivate, long TotalWorkingSet, long TotalPrivate, int Samples)
{
    public MemoryPeaks Max(MemoryPeaks other) => new(
        Math.Max(AgentWorkingSet, other.AgentWorkingSet), Math.Max(AgentPrivate, other.AgentPrivate),
        Math.Max(KopiaWorkingSet, other.KopiaWorkingSet), Math.Max(KopiaPrivate, other.KopiaPrivate),
        Math.Max(TotalWorkingSet, other.TotalWorkingSet), Math.Max(TotalPrivate, other.TotalPrivate), Samples + other.Samples);
}

/// <summary>
/// Samples the working set and private bytes of every process in the agent's job (the agent, its kopia server and
/// any kopia CLI it runs) every <see cref="Interval"/>, and keeps the peaks of the current phase. Kopia processes
/// are summed, since a check runs a CLI process beside the server.
/// </summary>
internal sealed class ResourceMonitor : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly AgentProcess _agent;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Task _sampling;
    private MemoryPeaks _phase = new(0, 0, 0, 0, 0, 0, 0);

    public ResourceMonitor(AgentProcess agent)
    {
        _agent = agent;
        _sampling = Task.Run(SampleLoopAsync);
    }

    /// <summary>Returns the peaks since the last call (or since the start) and begins a new phase.</summary>
    public MemoryPeaks EndPhase()
    {
        lock (_lock)
        {
            var peaks = _phase;
            _phase = new MemoryPeaks(0, 0, 0, 0, 0, 0, 0);
            return peaks;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _sampling;
        _stop.Dispose();
    }

    private async Task SampleLoopAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                var sample = Sample();
                if (sample is not null)
                {
                    lock (_lock)
                    {
                        _phase = _phase.Max(sample);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private MemoryPeaks? Sample()
    {
        if (!_agent.IsRunning)
        {
            return null;
        }

        long agentWs = 0, agentPrivate = 0, kopiaWs = 0, kopiaPrivate = 0;
        try
        {
            foreach (var id in _agent.Job.ProcessIds())
            {
                try
                {
                    using var process = Process.GetProcessById(id);
                    var (ws, priv) = (process.WorkingSet64, process.PrivateMemorySize64);
                    if (string.Equals(process.ProcessName, AgentProcess.KopiaProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        kopiaWs += ws;
                        kopiaPrivate += priv;
                    }
                    else if (id == _agent.Process.Id)
                    {
                        agentWs = ws;
                        agentPrivate = priv;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Exited between the listing and the read.
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // The agent is being restarted.
            return null;
        }

        return new MemoryPeaks(agentWs, agentPrivate, kopiaWs, kopiaPrivate, agentWs + kopiaWs, agentPrivate + kopiaPrivate, 1);
    }
}
