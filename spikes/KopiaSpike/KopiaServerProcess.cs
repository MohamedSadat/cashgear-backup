using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace KopiaSpike;

public sealed record KopiaServerOptions(
    string KopiaExe,
    SpikeWorkspace Workspace,
    SpikeCertificate Certificate,
    SpikeSecrets Secrets,
    bool PassRepositoryPassword,
    string Name);

public sealed class KopiaServerStartException(string message, int? exitCode, IReadOnlyList<string> outputTail)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
    public IReadOnlyList<string> OutputTail { get; } = outputTail;
}

/// <summary>
/// One <c>kopia server start</c> child process: credentials via environment only, stdin held open
/// (<c>--shutdown-on-stdin</c>), stdout/stderr drained to a log file, address read from stderr.
/// </summary>
public sealed class KopiaServerProcess : IAsyncDisposable
{
    private const string AddressPrefix = "SERVER ADDRESS: ";

    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly TaskCompletionSource<Uri> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _disposed;

    private KopiaServerProcess(Process process, string logPath)
    {
        _process = process;
        _log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _stdoutPump = PumpAsync(process.StandardOutput, "out");
        _stderrPump = PumpAsync(process.StandardError, "err");
    }

    public Uri BaseAddress { get; private set; } = null!;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static IReadOnlyList<string> BuildArguments(SpikeWorkspace ws, SpikeCertificate cert) =>
    [
        "server", "start",
        $"--config-file={ws.ConfigFile}",
        $"--cache-directory={ws.Cache}",
        $"--log-dir={ws.KopiaLogs}",
        "--address=127.0.0.1:0",
        $"--tls-cert-file={cert.CertPath}",
        $"--tls-key-file={cert.KeyPath}",
        "--ui",                      // --no-ui would also remove the REST API
        $"--html={ws.Html}",         // ...so serve a stub page instead of the web UI
        "--no-grpc",
        "--shutdown-on-stdin",
        "--no-persist-credentials",
        "--no-use-credential-manager",
    ];

    public static async Task<KopiaServerProcess> StartAsync(KopiaServerOptions options, TimeSpan timeout, CancellationToken ct)
    {
        var ws = options.Workspace;
        var psi = new ProcessStartInfo(options.KopiaExe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ws.Root,
        };
        foreach (var arg in BuildArguments(ws, options.Certificate))
        {
            psi.ArgumentList.Add(arg);
        }

        // Never inherit KOPIA_* settings from the machine; pass secrets only through the child's environment.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            psi.Environment.Remove(key);
        }

        psi.Environment["KOPIA_SERVER_USERNAME"] = options.Secrets.UiUser;
        psi.Environment["KOPIA_SERVER_PASSWORD"] = options.Secrets.UiPassword;
        psi.Environment["KOPIA_SERVER_CONTROL_USER"] = options.Secrets.ControlUser;
        psi.Environment["KOPIA_SERVER_CONTROL_PASSWORD"] = options.Secrets.ControlPassword;
        psi.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
        if (options.PassRepositoryPassword)
        {
            psi.Environment["KOPIA_PASSWORD"] = options.Secrets.RepositoryPassword;
        }

        var logPath = Path.Combine(ws.Artifacts, $"kopia-{options.Name}.log");
        var server = new KopiaServerProcess(Process.Start(psi)!, logPath);
        try
        {
            await server.WaitForAddressAsync(timeout, ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Graceful stop, escalating: control API shutdown, then closing stdin, then killing the tree.
    /// Returns which step stopped the process.
    /// </summary>
    public async Task<string> ShutdownAsync(Func<CancellationToken, Task>? requestShutdown, CancellationToken ct)
    {
        if (_process.HasExited)
        {
            return "already-exited";
        }

        if (requestShutdown is not null)
        {
            try
            {
                await requestShutdown(ct);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(10), ct))
                {
                    return "control-api";
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to the next step.
            }
        }

        _process.StandardInput.Close();
        if (await WaitForExitAsync(TimeSpan.FromSeconds(5), ct))
        {
            return "stdin-closed";
        }

        _process.Kill(entireProcessTree: true);
        await WaitForExitAsync(TimeSpan.FromSeconds(5), ct);
        return "killed";
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(TimeSpan.FromSeconds(5)));
        lock (_log)
        {
            _disposed = true;
            _log.Dispose();
        }

        _process.Dispose();
    }

    private async Task WaitForAddressAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var exited = _process.WaitForExitAsync(timeoutCts.Token);
        var done = await Task.WhenAny(_address.Task, exited, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        if (done == _address.Task)
        {
            BaseAddress = await _address.Task;
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (_process.HasExited)
        {
            await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(TimeSpan.FromSeconds(2), ct));
            throw new KopiaServerStartException(
                $"kopia server exited with code {_process.ExitCode} before printing its address.", _process.ExitCode, Tail());
        }

        throw new KopiaServerStartException($"kopia server printed no address within {timeout.TotalSeconds:0} s.", null, Tail());
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PumpAsync(StreamReader reader, string stream)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _lines.Enqueue(line);
            lock (_log)
            {
                if (!_disposed)
                {
                    _log.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{stream}] {line}");
                }
            }

            if (line.StartsWith(AddressPrefix, StringComparison.Ordinal)
                && Uri.TryCreate(line[AddressPrefix.Length..].Trim(), UriKind.Absolute, out var address))
            {
                _address.TrySetResult(address);
            }
        }
    }

    private List<string> Tail() => [.. _lines.TakeLast(15)];
}
