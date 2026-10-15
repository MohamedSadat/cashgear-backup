using System.Collections.Concurrent;
using System.Net.Sockets;
using CashGear.Backup.Agent.Destinations;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;

namespace CashGear.Backup.Agent.Engine;

internal enum DestinationConnection
{
    Connecting,
    Connected,
    Unavailable,
}

internal sealed record DestinationStatus(DestinationConnection State, string? Message)
{
    public static DestinationStatus Connecting { get; } = new(DestinationConnection.Connecting, null);
    public static DestinationStatus Connected { get; } = new(DestinationConnection.Connected, null);
}

/// <summary>
/// Owns the one <see cref="IBackupEngine"/> and knows which destinations it has connected. Passwords are
/// decrypted from state.db only for the moment they are handed to the engine.
/// </summary>
internal sealed partial class EngineHost(IBackupEngineFactory factory, ISecretProtector protector, AgentOptions options, ILogger<EngineHost> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, DestinationStatus> _status = new();
    private IBackupEngine? _engine;

    public IBackupEngine Engine => _engine ?? throw new InvalidOperationException("The engine has not started.");

    public void Start(string machineIdentity)
    {
        _engine = factory.Create(new BackupEngineSettings
        {
            DataDirectory = options.EngineDirectory,
            MachineIdentity = machineIdentity,
            ExecutablePath = options.EngineExecutablePath,
        });
    }

    public DestinationStatus StatusOf(Guid destinationId) => _status.GetValueOrDefault(destinationId, DestinationStatus.Connecting);

    public bool IsConnected(Guid destinationId) => StatusOf(destinationId).State == DestinationConnection.Connected;

    public void MarkConnected(Guid destinationId) => _status[destinationId] = DestinationStatus.Connected;

    /// <summary>Connects the destination's repository if it is not connected yet (cheap when it is).</summary>
    public async Task<IBackupEngine> ConnectAsync(DestinationRecord destination, CancellationToken ct)
    {
        try
        {
            using (var password = protector.Unprotect(destination.ProtectedPassword))
            {
                await WithSpecAsync(destination, spec => Engine.ConnectRepositoryAsync(spec, password, ct));
            }

            if (_status.TryGetValue(destination.Id, out var previous) && previous.State == DestinationConnection.Unavailable)
            {
                LogReconnected(logger, destination.Id, destination.Location);
            }

            _status[destination.Id] = DestinationStatus.Connected;
            return Engine;
        }
        catch (BackupEngineException ex)
        {
            _status[destination.Id] = new DestinationStatus(DestinationConnection.Unavailable, ex.Message);
            LogUnavailable(logger, destination.Id, destination.Location, ex.Kind, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the engine's view of the destination. Its credential is decrypted for the
    /// call only (the engine keeps its own copies where it must), and a network share is opened first.
    /// </summary>
    public async Task<T> WithSpecAsync<T>(DestinationRecord destination, Func<DestinationSpec, Task<T>> action)
    {
        using var credential = destination.ProtectedCredential is { } blob ? protector.Unprotect(blob) : null;
        var settings = DestinationSettings.FromJson(destination.Settings);
        switch (destination.Kind)
        {
            case DestinationKind.FileSystem:
                return await action(new FileSystemDestination(destination.Id, destination.Location));
            case DestinationKind.NetworkShare:
                ShareConnection.Connect(destination.Location, settings.Username ?? "",
                    credential ?? throw new BackupEngineException(BackupErrorKind.DestinationAccessDenied, "The network share's password is missing."));
                return await action(new FileSystemDestination(destination.Id, destination.Location));
            case DestinationKind.Sftp or DestinationKind.S3:
                var input = new DestinationInput(destination.Name, destination.Kind, destination.Location, settings, credential);
                return await action(input.Spec(destination.Id,
                    credential ?? throw new BackupEngineException(BackupErrorKind.DestinationAccessDenied, "The destination's credential is missing.")));
            default:
                throw new NotSupportedException($"Destination kind {destination.Kind} is not supported.");
        }
    }

    public Task WithSpecAsync(DestinationRecord destination, Func<DestinationSpec, Task> action) =>
        WithSpecAsync(destination, async spec =>
        {
            await action(spec);
            return true;
        });

    /// <summary>
    /// A quick check before a run, so an unplugged disk or an offline server is a clear failed run rather than an
    /// engine error: the folder exists, the share opens, or the server accepts a connection.
    /// </summary>
    public async Task<bool> IsReachableAsync(DestinationRecord destination, CancellationToken ct)
    {
        try
        {
            switch (destination.Kind)
            {
                case DestinationKind.FileSystem:
                    return Directory.Exists(destination.Location);
                case DestinationKind.NetworkShare:
                    return await WithSpecAsync(destination, _ => Task.FromResult(Directory.Exists(destination.Location)));
                case DestinationKind.Sftp:
                    var sftp = DestinationSettings.FromJson(destination.Settings);
                    return await AcceptsConnectionAsync(sftp.Host ?? "", sftp.Port == 0 ? 22 : sftp.Port, ct);
                case DestinationKind.S3:
                    var s3 = DestinationSettings.FromJson(destination.Settings);
                    var (host, port) = HostAndPort(s3.Endpoint ?? "", s3.InsecureHttp ? 80 : 443);
                    return await AcceptsConnectionAsync(host, port, ct);
                default:
                    return true;
            }
        }
        catch (BackupEngineException)
        {
            return false;
        }
    }

    private static async Task<bool> AcceptsConnectionAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static (string Host, int Port) HostAndPort(string endpoint, int defaultPort)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out var port) ? (endpoint[..colon], port) : (endpoint, defaultPort);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _engine, null) is { } engine)
        {
            await engine.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Destination {DestinationId} at {Location} is unavailable ({Kind}): {Reason}")]
    private static partial void LogUnavailable(ILogger logger, Guid destinationId, string location, BackupErrorKind kind, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Destination {DestinationId} at {Location} is available again")]
    private static partial void LogReconnected(ILogger logger, Guid destinationId, string location);
}
