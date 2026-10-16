using System.IO;
using System.Windows;
using CashGear.Backup.Client.Elevation;
using CashGear.Backup.Client.Security;

namespace CashGear.Backup.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started by ourselves with "runas" for a Manage call: no window, no tray icon, no single-instance check.
        if (HelperArguments.IsHelperInvocation(args))
        {
            return RunElevatedHelper(args);
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.ActivateFirst();
            return 0;
        }

        var app = new App { Instance = instance };
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// The helper runs inside a window-less WPF application only so that Main can stay a plain STA entry point
    /// without blocking on the async helper loop.
    /// </summary>
    private static int RunElevatedHelper(string[] args)
    {
        var arguments = HelperArguments.Parse(args);
        if (arguments is null)
        {
            return ElevatedHelper.ExitBadArguments;
        }

        var verifier = new AgentServerVerifier(new AgentServerTrust { SameUser = arguments.TrustSameUserAgent });
        var helper = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        helper.Startup += async (_, _) =>
        {
            int exitCode;
            try
            {
                exitCode = await ElevatedHelper.RunAsync(arguments, verifier, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                exitCode = ElevatedHelper.ExitNoApp;
            }

            helper.Shutdown(exitCode);
        };
        return helper.Run();
    }
}
