using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.UI.Components.Services;

/// <summary>How the app sees the agent right now.</summary>
public enum AgentConnection
{
    /// <summary>Not asked yet.</summary>
    Unknown,

    /// <summary>The service is not running (or its pipe did not answer).</summary>
    NotRunning,

    /// <summary>Something other than the service answered on its pipe; the app sends it nothing.</summary>
    NotTrusted,

    /// <summary>Running, still reconnecting its destinations after a start.</summary>
    Starting,

    Ready,
}

/// <summary>
/// The agent's status, shared by the window and the tray icon. The desktop shell polls it with
/// <see cref="RunAsync"/>; components read it and listen to <see cref="Changed"/> (raised on any thread).
/// </summary>
public sealed class AgentMonitor(IAgentClient agent)
{
    private readonly SemaphoreSlim _refresh = new(1, 1);

    public AgentConnection Connection { get; private set; }

    public AgentStatus? Status { get; private set; }

    /// <summary>Why the agent is not usable, for the user.</summary>
    public string? Problem { get; private set; }

    public bool IsReady => Connection == AgentConnection.Ready;

    /// <summary>Whether Manage calls go out without a UAC prompt.</summary>
    public bool CanManage => Status?.CallerCanManage == true;

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            AgentConnection connection;
            AgentStatus? status = null;
            string? problem = null;
            try
            {
                status = await agent.GetStatusAsync(ct);
                connection = status.Ready ? AgentConnection.Ready : AgentConnection.Starting;
            }
            catch (AgentCallException ex)
            {
                connection = ex.Kind == AgentErrorKinds.AgentNotTrusted ? AgentConnection.NotTrusted : AgentConnection.NotRunning;
                problem = ex.Message;
            }

            var changed = connection != Connection || !Equals(status, Status) || problem != Problem;
            (Connection, Status, Problem) = (connection, status, problem);
            if (changed)
            {
                Changed?.Invoke();
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Polls until <paramref name="ct"/> fires: often while the agent is missing or starting, less often once it is ready.</summary>
    public async Task RunAsync(TimeSpan whenReady, TimeSpan whenWaiting, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await RefreshAsync(ct);
                await Task.Delay(IsReady ? whenReady : whenWaiting, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
