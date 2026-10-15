using System.Windows;
using CashGear.Backup.Client;
using CashGear.Backup.Client.Elevation;
using CashGear.Backup.Client.Security;
using CashGear.Backup.UI.Components.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.Desktop;

/// <summary>
/// The desktop app: runs as the user, talks to the agent over its pipe, keeps a tray icon while the window is
/// closed, and asks for UAC elevation (through the helper) only for Manage calls.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private ServiceProvider? _services;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    /// <summary>Set by <see cref="Program"/>; lets a second start bring this window forward.</summary>
    internal SingleInstance? Instance { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = BuildServices(DesktopSettings.FromEnvironment());

        var monitor = _services.GetRequiredService<AgentMonitor>();
        _ = monitor.RunAsync(whenReady: TimeSpan.FromSeconds(10), whenWaiting: TimeSpan.FromSeconds(3), _lifetime.Token);

        _tray = new TrayIcon(_services.GetRequiredService<IAgentClient>(), monitor, ShowMainWindow, ExitAsync);
        _ = _tray.RunAsync();

        Instance?.OnActivate(() => Dispatcher.InvokeAsync(ShowMainWindow));
        SessionEnding += async (_, _) => await ExitAsync();

        // Started at sign-in (the installer adds that in Phase 4): just the tray icon.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            ShowMainWindow();
        }
    }

    private static ServiceProvider BuildServices(DesktopSettings settings)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddBackupUi();
        services.AddSingleton<IAgentServerVerifier>(new AgentServerVerifier(new AgentServerTrust { SameUser = settings.TrustSameUserAgent }));
        services.AddSingleton<IHelperLauncher>(new UacHelperLauncher(
            Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown."),
            settings.AgentPipeName,
            settings.TrustSameUserAgent));
        services.AddSingleton(sp => new ElevatedSession(sp.GetRequiredService<IHelperLauncher>()));
        services.AddSingleton<IManageRelay>(sp => sp.GetRequiredService<ElevatedSession>());
        services.AddSingleton<IAgentClient>(sp => new AgentClient(
            new AgentClientOptions { PipeName = settings.AgentPipeName, Verifier = sp.GetRequiredService<IAgentServerVerifier>() },
            sp.GetRequiredService<IManageRelay>()));
        services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        services.AddSingleton<IDesktopShell, DesktopShell>();
        return services.BuildServiceProvider();
    }

    private void ShowMainWindow()
    {
        if (_exiting || _services is null)
        {
            return;
        }

        _window ??= new MainWindow(_services);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        await _lifetime.CancelAsync();
        _tray?.Dispose();
        _window?.CloseForExit();
        if (_services is not null)
        {
            // Closes the agent channel and, if one is open, the elevated helper's pipe (which ends the helper).
            await _services.DisposeAsync();
        }

        Shutdown();
    }
}
