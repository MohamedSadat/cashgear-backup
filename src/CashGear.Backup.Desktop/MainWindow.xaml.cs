using System.ComponentModel;
using System.Windows;

namespace CashGear.Backup.Desktop;

/// <summary>The app window: the UI.Components pages in a BlazorWebView. Closing it keeps the app in the tray.</summary>
public partial class MainWindow : Window
{
    private bool _exiting;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        WebView.Services = services;
        WebView.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = DesktopSettings.WebViewDataFolder;
    }

    /// <summary>Closes the window for real (Exit from the tray menu).</summary>
    internal void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
