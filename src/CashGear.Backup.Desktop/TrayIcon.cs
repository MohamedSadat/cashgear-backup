using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Backup.Client;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Model;
using CashGear.Backup.UI.Components.Services;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace CashGear.Backup.Desktop;

/// <summary>
/// The notification-area icon: overall status in its colour and tooltip, and a menu to open the window, back up
/// every job now, or exit. Lives on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan s_refresh = TimeSpan.FromSeconds(30);

    private readonly IAgentClient _agent;
    private readonly AgentMonitor _monitor;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _backUpNow;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<Job>? _jobs;
    private OverviewLevel? _shownLevel;

    public TrayIcon(IAgentClient agent, AgentMonitor monitor, Action open, Func<Task> exit)
    {
        _agent = agent;
        _monitor = monitor;
        var openItem = new MenuItem { Header = "Open CashGear Backup", FontWeight = FontWeights.SemiBold };
        openItem.Click += (_, _) => open();
        _backUpNow = new MenuItem { Header = "Back up now", IsEnabled = false };
        _backUpNow.Click += async (_, _) => await BackUpAllAsync();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += async (_, _) => await exit();

        _icon = new TaskbarIcon
        {
            ToolTipText = "CashGear Backup",
            IconSource = IconFor(OverviewLevel.Info),
            ContextMenu = new ContextMenu { Items = { openItem, _backUpNow, new Separator(), exitItem } },
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayMouseDoubleClick += (_, _) => open();
        _icon.TrayLeftMouseUp += (_, _) => open();
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _monitor.Changed += OnMonitorChanged;
    }

    /// <summary>Keeps the status fresh until disposed: every 30 seconds and whenever the agent's state changes.</summary>
    public async Task RunAsync()
    {
        using var timer = new PeriodicTimer(s_refresh);
        try
        {
            do
            {
                await UpdateAsync();
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        _cts.Cancel();
        _cts.Dispose();
        _icon.Dispose();
    }

    private void OnMonitorChanged() => _icon.Dispatcher.InvokeAsync(UpdateAsync);

    private async Task UpdateAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        IReadOnlyList<Job>? jobs = null;
        if (_monitor.IsReady)
        {
            try
            {
                jobs = await _agent.ListJobsAsync(_cts.Token);
            }
            catch (AgentCallException)
            {
                // The monitor notices and changes the status.
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
        }

        _jobs = jobs;
        var overview = BackupOverview.Summarize(_monitor.Connection, jobs);
        _icon.ToolTipText = $"CashGear Backup: {overview.Text}";
        if (_shownLevel != overview.Level)
        {
            _shownLevel = overview.Level;
            _icon.IconSource = IconFor(overview.Level);
        }

        _backUpNow.IsEnabled = _monitor.IsReady && jobs is { Count: > 0 };
    }

    /// <summary>Starts every enabled job that is not already running.</summary>
    private async Task BackUpAllAsync()
    {
        var started = new List<string>();
        var problems = new List<string>();
        foreach (var job in (_jobs ?? []).Where(j => j.Spec.Enabled && j.LastRun?.State != RunState.Running))
        {
            try
            {
                await _agent.RunJobNowAsync(job.Id, _cts.Token);
                started.Add(job.Spec.Name);
            }
            catch (AgentCallException ex)
            {
                problems.Add($"{job.Spec.Name}: {ErrorText.For(ex)}");
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
        }

        var message = started.Count == 0 && problems.Count == 0
            ? "Nothing to start: every backup is running or paused."
            : string.Join(Environment.NewLine, [.. started.Select(n => $"Backing up {n}"), .. problems]);
        _icon.ShowNotification("CashGear Backup", message, problems.Count > 0 ? NotificationIcon.Warning : NotificationIcon.Info);
        await UpdateAsync();
    }

    private static GeneratedIconSource IconFor(OverviewLevel level) => new()
    {
        Text = "CG",
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(level switch
        {
            OverviewLevel.Ok => Color.FromRgb(0x1A, 0x7F, 0x37),
            OverviewLevel.Warning => Color.FromRgb(0xB0, 0x70, 0x00),
            OverviewLevel.Problem => Color.FromRgb(0xB4, 0x23, 0x18),
            _ => Color.FromRgb(0x1F, 0x6F, 0xEB),
        }),
    };
}
