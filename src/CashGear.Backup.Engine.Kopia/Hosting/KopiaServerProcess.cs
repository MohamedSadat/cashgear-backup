using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CashGear.Backup.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia.Hosting;

/// <summary>Kopia's files for one repository, all inside our (SYSTEM/Administrators-only) data folder.</summary>
internal sealed record KopiaRepositoryLayout(string Root)
{
    public string ConfigFile => Path.Combine(Root, "repository.config");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Tls => Path.Combine(Root, "tls");

    public static KopiaRepositoryLayout For(string dataDirectory, Guid repositoryId) =>
        new(Path.Combine(dataDirectory, "repos", repositoryId.ToString("D")));

    public void CreateDirectories()
    {
        foreach (var dir in new[] { Root, Cache, Logs, Tls })
        {
            Directory.CreateDirectory(dir);
        }
    }
}

internal sealed record KopiaServerStartInfo(
    string ExePath,
    KopiaRepositoryLayout Layout,
    string HtmlDirectory,
    KopiaServerCertificate Certificate,
    KopiaServerCredentials Credentials,
    SecureSecret? RepositoryPassword,
    string Name)
{
    /// <summary>Storage credentials read from the environment (S3 keys).</summary>
    public IReadOnlyDictionary<string, SecureSecret> StorageEnvironment { get; init; } = new Dictionary<string, SecureSecret>();
}

internal sealed class KopiaServerStartException(string message, int? exitCode, IReadOnlyList<string> outputTail)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
    public IReadOnlyList<string> OutputTail { get; } = outputTail;
}

/// <summary>
/// One <c>kopia server start</c> child process in a kill-on-close Job Object: credentials via environment only,
/// stdin held open (<c>--shutdown-on-stdin</c>), stdout/stderr drained to the logger, address read from stderr.
/// </summary>
internal sealed partial class KopiaServerProcess : IAsyncDisposable
{
    private const string AddressPrefix = "SERVER ADDRESS: ";
    private const int TailLines = 20;

    private readonly Process _process;
    private readonly JobObject _job;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly string[] _secrets;
    private readonly ConcurrentQueue<string> _tail = new();
    private readonly TaskCompletionSource<Uri> _address = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _disposed;
    private int? _exitCodeAfterDispose;

    private KopiaServerProcess(Process process, JobObject job, ILogger logger, string name, IEnumerable<string> secrets)
    {
        _process = process;
        _job = job;
        _logger = logger;
        _name = name;
        _secrets = [.. secrets];
        _stdoutPump = PumpAsync(process.StandardOutput);
        _stderrPump = PumpAsync(process.StandardError);
    }

    public Uri BaseAddress { get; private set; } = null!;
    public int ProcessId => _process.Id;
    public bool HasExited => _disposed || _process.HasExited;
    public int? ExitCode => _disposed ? _exitCodeAfterDispose : _process.HasExited ? _process.ExitCode : null;

    /// <summary>Test support: the job the process was assigned to.</summary>
    internal JobObject Job => _job;

    internal Process Process => _process;

    /// <summary>The launch line. FINDINGS.md explains each flag.</summary>
    public static IReadOnlyList<string> BuildArguments(KopiaRepositoryLayout layout, string htmlDirectory, KopiaServerCertificate certificate) =>
    [
        "server", "start",
        $"--config-file={layout.ConfigFile}",
        $"--cache-directory={layout.Cache}",
        $"--log-dir={layout.Logs}",
        "--address=127.0.0.1:0",
        $"--tls-cert-file={certificate.CertPath}",
        $"--tls-key-file={certificate.KeyPath}",
        "--ui",                     // --no-ui would also remove the REST API
        $"--html={htmlDirectory}",  // ...so serve our stub page instead of the web UI
        "--no-grpc",
        "--shutdown-on-stdin",
        "--no-persist-credentials",
        "--no-use-credential-manager",
    ];

    public static async Task<KopiaServerProcess> StartAsync(KopiaServerStartInfo info, KopiaPin pin, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(info.ExePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = info.Layout.Root,
        };
        foreach (var arg in BuildArguments(info.Layout, info.HtmlDirectory, info.Certificate))
        {
            psi.ArgumentList.Add(arg);
        }

        ConfigureEnvironment(psi.Environment, info.Credentials, info.RepositoryPassword, info.StorageEnvironment);

        var job = new JobObject();
        Process process;
        try
        {
            // Hold kopia.exe open (no writers, no deletes) from the hash check until the process exists.
            await using (await pin.OpenVerifiedAsync(info.ExePath, ct))
            {
                process = Process.Start(psi) ?? throw new KopiaServerStartException("kopia server did not start.", null, []);
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            psi.Environment.Remove("KOPIA_PASSWORD");
            foreach (var key in info.StorageEnvironment.Keys)
            {
                psi.Environment.Remove(key);
            }
        }

        try
        {
            job.Assign(process);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            job.Dispose();
            throw;
        }

        var server = new KopiaServerProcess(process, job, logger, info.Name, info.Credentials.Passwords);
        LogStarted(logger, info.Name, process.Id);
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
    /// The child's environment: the inherited one minus every KOPIA_* and AWS_* variable (so nothing set on the
    /// machine changes Kopia's behaviour or lends it other credentials), plus the server credentials, once a config
    /// exists the repository password, and the storage credentials of this repository. Secrets never go on the
    /// command line.
    /// </summary>
    public static void ConfigureEnvironment(IDictionary<string, string?> environment, KopiaServerCredentials credentials, SecureSecret? repositoryPassword,
        IReadOnlyDictionary<string, SecureSecret>? storageEnvironment = null)
    {
        RemoveInherited(environment);
        foreach (var (key, value) in storageEnvironment ?? new Dictionary<string, SecureSecret>())
        {
            environment[key] = value.Reveal();
        }

        environment["KOPIA_SERVER_USERNAME"] = credentials.UiUser;
        environment["KOPIA_SERVER_PASSWORD"] = credentials.UiPassword;
        environment["KOPIA_SERVER_CONTROL_USER"] = credentials.ControlUser;
        environment["KOPIA_SERVER_CONTROL_PASSWORD"] = credentials.ControlPassword;
        environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
        if (repositoryPassword is not null)
        {
            environment["KOPIA_PASSWORD"] = repositoryPassword.Reveal();
        }
    }

    /// <summary>Variables that would change Kopia's behaviour or its storage credentials.</summary>
    public static void RemoveInherited(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(k => k.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(key);
        }
    }

    public IReadOnlyList<string> OutputTail() => [.. _tail];

    /// <summary>
    /// Graceful stop, escalating: control API shutdown, then closing stdin, then killing the process.
    /// Returns which step stopped it.
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
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await requestShutdown(requestTimeout.Token);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(10), ct))
                {
                    return "control-api";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // Fall through to the next step.
            }
            catch (Exception ex) when (ex is Api.KopiaApiException)
            {
                // Fall through to the next step.
            }
        }

        try
        {
            _process.StandardInput.Close();
            if (await WaitForExitAsync(TimeSpan.FromSeconds(5), ct))
            {
                return "stdin-closed";
            }
        }
        catch (IOException)
        {
            // Pipe already broken; kill below.
        }

        Kill();
        await WaitForExitAsync(TimeSpan.FromSeconds(5), ct);
        return "killed";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_process.HasExited)
        {
            Kill();
            await WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(TimeSpan.FromSeconds(5)));
        _job.Dispose();
        _exitCodeAfterDispose = _process.HasExited ? _process.ExitCode : null;
        _disposed = true;
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
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
            await Task.WhenAny(Task.WhenAll(_stdoutPump, _stderrPump), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            throw new KopiaServerStartException(
                $"kopia server exited with code {_process.ExitCode} before printing its address: {LastLine()}", _process.ExitCode, OutputTail());
        }

        throw new KopiaServerStartException($"kopia server printed no address within {timeout.TotalSeconds:0} s.", null, OutputTail());
    }

    private string LastLine() => _tail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "(no output)";

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

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } raw)
        {
            var line = Scrub(raw);
            _tail.Enqueue(line);
            while (_tail.Count > TailLines && _tail.TryDequeue(out _))
            {
            }

            LogOutput(_logger, _name, line);
            if (line.StartsWith(AddressPrefix, StringComparison.Ordinal)
                && Uri.TryCreate(line[AddressPrefix.Length..].Trim(), UriKind.Absolute, out var address))
            {
                _address.TrySetResult(address);
            }
        }
    }

    // Kopia was never seen printing a credential; this is defence in depth for the values we hold as strings.
    private string Scrub(string line)
    {
        foreach (var secret in _secrets)
        {
            line = line.Replace(secret, "***", StringComparison.Ordinal);
        }

        return line;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kopia server for repository {Name} started (pid {ProcessId})")]
    private static partial void LogStarted(ILogger logger, string name, int processId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "kopia[{Name}]: {Line}")]
    private static partial void LogOutput(ILogger logger, string name, string line);
}
