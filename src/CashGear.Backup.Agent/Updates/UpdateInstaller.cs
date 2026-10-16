using System.Diagnostics;

namespace CashGear.Backup.Agent.Updates;

/// <summary>Starts the installation of a verified update.</summary>
internal interface IUpdateInstaller
{
    /// <summary>Starts it and returns; the installer stops and restarts this service itself.</summary>
    Task StartAsync(string msiPath, string logPath, CancellationToken ct);
}

/// <summary>
/// msiexec from System32, silent, no reboot, with a verbose log. It is not a child that dies with the service: the
/// MSI's own upgrade stops this service, replaces the files and starts it again.
/// </summary>
internal sealed class MsiexecInstaller : IUpdateInstaller
{
    public Task StartAsync(string msiPath, string logPath, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/i", msiPath, "/qn", "/norestart", "/l*v", logPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("msiexec did not start.");
        return Task.CompletedTask;
    }
}
