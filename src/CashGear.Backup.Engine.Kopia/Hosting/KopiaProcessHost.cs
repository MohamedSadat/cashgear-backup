using System.Diagnostics;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia.Hosting;

internal sealed record KopiaProcessHostOptions
{
    public required string KopiaExePath { get; init; }
    public required KopiaRepositoryLayout Layout { get; init; }
    public required string HtmlDirectory { get; init; }

    /// <summary>Used in log lines; the repository id.</summary>
    public required string Name { get; init; }

    public KopiaPin Pin { get; init; } = KopiaPin.Embedded;
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive failed health checks (process still alive) before a restart. An exited process restarts at once.</summary>
    public int UnhealthyChecksBeforeRestart { get; init; } = 2;

    public TimeSpan RestartBackoffInitial { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RestartBackoffMax { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long callers wait for a restart in progress before getting EngineUnavailable.</summary>
    public TimeSpan ReadyWaitTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

internal enum KopiaHostState
{
    NotStarted,
    Running,
    Restarting,
    Stopped,
    Faulted,
}

/// <summary>A live server and the client bound to it. <see cref="Generation"/> changes on every (re)start.</summary>
internal sealed record KopiaServerSession(KopiaApiClient Client, int Generation);

/// <summary>
/// Supervises the <c>kopia server</c> of one repository: verifies the pinned binary before every start, makes
/// fresh TLS and credentials per start, health-checks the control API, restarts with back-off, and stops
/// gracefully (control API, then stdin, then kill). Holds a copy of the repository password while the
/// repository is connected so it can restart the server.
/// </summary>
internal sealed partial class KopiaProcessHost : IAsyncDisposable
{
    private readonly KopiaProcessHostOptions _options;
    private readonly ILogger _logger;
    private readonly ILogger _serverLogger;
    private readonly ILogger _apiLogger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stopping = new();

    private SecureSecret? _password;
    private Instance? _current;
    private TaskCompletionSource _ready = NewReady();
    private Exception? _fault;
    private Task? _healthLoop;
    private int _generation;

    public KopiaProcessHost(KopiaProcessHostOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<KopiaProcessHost>();
        _serverLogger = loggerFactory.CreateLogger("CashGear.Backup.Engine.Kopia.KopiaServer");
        _apiLogger = loggerFactory.CreateLogger<KopiaApiClient>();
    }

    public KopiaHostState State { get; private set; } = KopiaHostState.NotStarted;

    public KopiaRepositoryLayout Layout => _options.Layout;

    public bool ConfigExists => File.Exists(_options.Layout.ConfigFile);

    /// <summary>Test support: the running process and its credentials.</summary>
    internal KopiaServerProcess? CurrentProcess
    {
        get
        {
            lock (_lock)
            {
                return _current?.Process;
            }
        }
    }

    internal KopiaServerCredentials? CurrentCredentials
    {
        get
        {
            lock (_lock)
            {
                return _current?.Credentials;
            }
        }
    }

    internal byte[] CurrentCertificatePinForTest()
    {
        lock (_lock)
        {
            return _current?.CertificatePin ?? throw new InvalidOperationException("Not running.");
        }
    }

    /// <summary>
    /// Starts the server and waits until it answers. Pass the repository password when the config file exists
    /// (Kopia then opens the repository at start); pass null for a server that will create or connect one.
    /// </summary>
    public async Task StartAsync(SecureSecret? repositoryPassword, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (State != KopiaHostState.NotStarted)
            {
                throw new InvalidOperationException($"The host is {State}; it can be started once.");
            }

            if (repositoryPassword is not null)
            {
                SetRepositoryPassword(repositoryPassword);
            }

            _options.Layout.CreateDirectories();
            var instance = await LaunchAsync(ct);
            Publish(instance, KopiaHostState.Running);
            _healthLoop = Task.Run(() => HealthLoopAsync(_stopping.Token), CancellationToken.None);
        }
        catch
        {
            State = KopiaHostState.Stopped;
            ClearPassword();
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Keeps a copy of the password for restarts, once the repository has been created or connected.</summary>
    public void SetRepositoryPassword(SecureSecret password)
    {
        var copy = password.Copy();
        lock (_lock)
        {
            _password?.Dispose();
            _password = copy;
        }
    }

    /// <summary>The current server, waiting for a restart in progress.</summary>
    public async Task<KopiaServerSession> GetSessionAsync(CancellationToken ct)
    {
        while (true)
        {
            Task ready;
            lock (_lock)
            {
                switch (State)
                {
                    case KopiaHostState.Running when _current is not null:
                        return new KopiaServerSession(_current.Client, _current.Generation);
                    case KopiaHostState.Faulted:
                        throw new BackupEngineException(BackupErrorKind.EngineUnavailable,
                            $"The backup engine for repository {_options.Name} stopped after a fatal error: {_fault?.Message}", _fault);
                    case KopiaHostState.NotStarted or KopiaHostState.Stopped:
                        throw new BackupEngineException(BackupErrorKind.EngineUnavailable, $"The backup engine for repository {_options.Name} is not running.");
                }

                ready = _ready.Task;
            }

            try
            {
                await ready.WaitAsync(_options.ReadyWaitTimeout, ct);
            }
            catch (TimeoutException ex)
            {
                throw new BackupEngineException(BackupErrorKind.EngineUnavailable,
                    $"The backup engine for repository {_options.Name} is restarting; try again later.", ex);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A failed or faulted restart: the next loop reports the state.
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        if (_healthLoop is not null)
        {
            await _healthLoop;
        }

        await _lifecycle.WaitAsync(ct);
        try
        {
            Instance? instance;
            lock (_lock)
            {
                instance = _current;
                _current = null;
                if (State != KopiaHostState.Faulted)
                {
                    State = KopiaHostState.Stopped;
                }

                _ready.TrySetCanceled(CancellationToken.None);
            }

            if (instance is not null)
            {
                var stopwatch = Stopwatch.StartNew();
                var how = await instance.Process.ShutdownAsync(instance.Client.ControlShutdownAsync, ct);
                LogStopped(_logger, _options.Name, how, stopwatch.ElapsedMilliseconds);
                await instance.DisposeAsync();
            }

            ClearPassword();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is KopiaHostState.Running or KopiaHostState.Restarting or KopiaHostState.Faulted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The job object kills the process when the instance is disposed below.
            }
        }

        Instance? leftover;
        lock (_lock)
        {
            leftover = _current;
            _current = null;
        }

        if (leftover is not null)
        {
            await leftover.DisposeAsync();
        }

        ClearPassword();
        _stopping.Dispose();
        _lifecycle.Dispose();
    }

    private async Task<Instance> LaunchAsync(CancellationToken ct)
    {
        SecureSecret? password;
        lock (_lock)
        {
            // Only pass the password once Kopia has a config to open; before that it would be unused.
            password = ConfigExists ? _password?.Copy() : null;
        }

        using (password)
        {
            var certificate = KopiaServerCertificate.Create(_options.Layout.Tls);
            var credentials = KopiaServerCredentials.Generate();
            KopiaServerProcess process;
            try
            {
                process = await KopiaServerProcess.StartAsync(
                    new KopiaServerStartInfo(_options.KopiaExePath, _options.Layout, _options.HtmlDirectory, certificate, credentials, password, _options.Name),
                    _options.Pin, _options.StartTimeout, _serverLogger, ct);
            }
            catch (KopiaServerStartException ex)
            {
                certificate.DeleteKeyFile();
                throw ClassifyStartFailure(ex);
            }
            catch
            {
                certificate.DeleteKeyFile();
                throw;
            }

            var client = new KopiaApiClient(process.BaseAddress, certificate.Pin, credentials, _apiLogger);
            try
            {
                await WaitUntilReadyAsync(client, ct);

                // Kopia has loaded the key by the time it answers over TLS; it never reads it again.
                certificate.DeleteKeyFile();
                await client.InitializeCsrfAsync(ct);
                return new Instance(process, client, credentials, certificate.Pin, Interlocked.Increment(ref _generation));
            }
            catch
            {
                certificate.DeleteKeyFile();
                client.Dispose();
                await process.DisposeAsync();
                throw;
            }
        }
    }

    private async Task WaitUntilReadyAsync(KopiaApiClient client, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await client.GetControlStatusAsync(ct);
                return;
            }
            catch (HttpRequestException ex) when (stopwatch.Elapsed < _options.StartTimeout)
            {
                LogNotReady(_logger, _options.Name, ex.Message);
                await Task.Delay(200, ct);
            }
        }
    }

    private BackupEngineException ClassifyStartFailure(KopiaServerStartException ex)
    {
        var output = string.Join(Environment.NewLine, ex.OutputTail);
        var kind = output.Contains("invalid repository password", StringComparison.OrdinalIgnoreCase) ? BackupErrorKind.InvalidPassword
            : output.Contains("unable to connect to storage", StringComparison.OrdinalIgnoreCase)
              || output.Contains("cannot access storage path", StringComparison.OrdinalIgnoreCase) ? BackupErrorKind.DestinationUnreachable
            : BackupErrorKind.EngineUnavailable;
        LogStartFailed(_logger, _options.Name, ex.ExitCode, output);
        return new BackupEngineException(kind, $"The backup engine for repository {_options.Name} did not start: {ex.Message}", ex);
    }

    private void Publish(Instance instance, KopiaHostState state)
    {
        lock (_lock)
        {
            _current = instance;
            State = state;
            _ready.TrySetResult();
        }
    }

    private async Task HealthLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.HealthCheckInterval);
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                Instance? instance;
                lock (_lock)
                {
                    instance = _current;
                }

                if (instance is null)
                {
                    continue;
                }

                if (instance.Process.HasExited)
                {
                    LogExited(_logger, _options.Name, instance.Process.ExitCode);
                }
                else if (await IsHealthyAsync(instance, stop))
                {
                    failures = 0;
                    continue;
                }
                else if (++failures < _options.UnhealthyChecksBeforeRestart)
                {
                    continue;
                }

                failures = 0;
                if (!await RestartAsync(instance, stop))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task<bool> IsHealthyAsync(Instance instance, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(_options.HealthCheckTimeout);
        try
        {
            await instance.Client.GetControlStatusAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or KopiaApiException or OperationCanceledException && !stop.IsCancellationRequested)
        {
            LogUnhealthy(_logger, _options.Name, ex.Message);
            return false;
        }
    }

    /// <summary>Replaces a dead or unresponsive server. Returns false when the host faulted and must not retry.</summary>
    private async Task<bool> RestartAsync(Instance failed, CancellationToken stop)
    {
        await _lifecycle.WaitAsync(stop);
        try
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_current, failed))
                {
                    return true;
                }

                _current = null;
                State = KopiaHostState.Restarting;
                _ready = NewReady();
            }

            if (!failed.Process.HasExited)
            {
                await failed.Process.ShutdownAsync(failed.Client.ControlShutdownAsync, stop);
            }

            await failed.DisposeAsync();

            var delay = _options.RestartBackoffInitial;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var instance = await LaunchAsync(stop);
                    Publish(instance, KopiaHostState.Running);
                    LogRestarted(_logger, _options.Name, attempt, instance.Generation);
                    return true;
                }
                catch (BackupEngineException ex) when (ex.Kind is BackupErrorKind.EngineBinaryRejected or BackupErrorKind.InvalidPassword)
                {
                    lock (_lock)
                    {
                        _fault = ex;
                        State = KopiaHostState.Faulted;
                        _ready.TrySetException(ex);
                    }

                    LogFaulted(_logger, ex, _options.Name);
                    return false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    LogRestartFailed(_logger, _options.Name, attempt, delay.TotalSeconds, ex.Message);
                    await Task.Delay(delay, stop);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RestartBackoffMax.Ticks));
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ClearPassword()
    {
        lock (_lock)
        {
            _password?.Dispose();
            _password = null;
        }
    }

    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Instance(KopiaServerProcess Process, KopiaApiClient Client, KopiaServerCredentials Credentials, byte[] CertificatePin, int Generation) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Process.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kopia server for repository {Name} stopped by {How} in {ElapsedMs} ms")]
    private static partial void LogStopped(ILogger logger, string name, string how, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "kopia server for repository {Name} not answering yet: {Reason}")]
    private static partial void LogNotReady(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "kopia server for repository {Name} failed to start (exit code {ExitCode}). Output:\n{Output}")]
    private static partial void LogStartFailed(ILogger logger, string name, int? exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "kopia server for repository {Name} exited unexpectedly with code {ExitCode}")]
    private static partial void LogExited(ILogger logger, string name, int? exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "kopia server for repository {Name} failed its health check: {Reason}")]
    private static partial void LogUnhealthy(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "kopia server for repository {Name} restarted after {Attempts} attempt(s), generation {Generation}")]
    private static partial void LogRestarted(ILogger logger, string name, int attempts, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "kopia server for repository {Name}: restart attempt {Attempt} failed, retrying in {DelaySeconds} s: {Reason}")]
    private static partial void LogRestartFailed(ILogger logger, string name, int attempt, double delaySeconds, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "kopia server for repository {Name} will not be restarted")]
    private static partial void LogFaulted(ILogger logger, Exception exception, string name);
}
