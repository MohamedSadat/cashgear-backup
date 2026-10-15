using System.Collections.Concurrent;
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
                await Engine.ConnectRepositoryAsync(ToSpec(destination), password, ct);
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

    public static DestinationSpec ToSpec(DestinationRecord destination) => destination.Kind switch
    {
        DestinationKind.FileSystem => new FileSystemDestination(destination.Id, destination.Location),
        _ => throw new NotSupportedException($"Destination kind {destination.Kind} is not supported."),
    };

    /// <summary>A quick check before a run, so an unplugged disk is a clear failed run rather than an engine error.</summary>
    public static bool IsReachable(DestinationRecord destination) => destination.Kind switch
    {
        DestinationKind.FileSystem => Directory.Exists(destination.Location),
        _ => true,
    };

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
