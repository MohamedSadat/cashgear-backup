using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Grpc.Core;

namespace CashGear.Backup.Client.Elevation;

/// <summary>Starts the elevated helper; the test seam for <see cref="ElevatedSession"/>.</summary>
public interface IHelperLauncher
{
    /// <summary>
    /// Starts a helper that connects to <paramref name="pipeName"/>; returns its process id. Throws
    /// <see cref="AgentCallException"/> with <see cref="AgentErrorKinds.ElevationCanceled"/> when the user says no.
    /// </summary>
    Task<int> LaunchAsync(string pipeName, CancellationToken ct);
}

/// <summary>Starts the desktop app again in helper mode with the "runas" verb: Windows shows the UAC prompt.</summary>
[SupportedOSPlatform("windows")]
public sealed class UacHelperLauncher(string executablePath, string agentPipeName, bool trustSameUserAgent) : IHelperLauncher
{
    private const int ErrorCancelled = 1223;

    public async Task<int> LaunchAsync(string pipeName, CancellationToken ct)
    {
        var arguments = new HelperArguments(pipeName, Environment.ProcessId) { AgentPipeName = agentPipeName, TrustSameUserAgent = trustSameUserAgent };
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };
        foreach (var argument in arguments.ToArguments())
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            // ShellExecuteEx returns once the user has answered the prompt; keep that wait off the UI thread.
            using var process = await Task.Run(() => Process.Start(start), ct)
                ?? throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed, "Windows did not start the administrator helper.");
            return process.Id;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new AgentCallException(StatusCode.Cancelled, AgentErrorKinds.ElevationCanceled,
                "Administrator approval was not given, so nothing was changed.", ex);
        }
        catch (Win32Exception ex)
        {
            throw new AgentCallException(StatusCode.Unavailable, AgentErrorKinds.ElevationFailed,
                "Windows could not start the administrator helper: " + ex.Message, ex);
        }
    }
}
