using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Destinations;

/// <summary>Adds repositories (new or existing) and remembers them with their DPAPI-protected password.</summary>
internal sealed partial class DestinationService(
    IDbContextFactory<StateDbContext> dbs, EngineHost engines, ISecretProtector protector, TimeProvider time, ILogger<DestinationService> logger)
{
    /// <summary>The shortest password accepted for a new repository; there is no way to recover a lost one.</summary>
    public const int MinimumPasswordLength = 8;

    public async Task<IReadOnlyList<(DestinationRecord Destination, DestinationStatus Status)>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var destinations = await db.Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return [.. destinations.Select(d => (d, engines.StatusOf(d.Id)))];
    }

    /// <summary>
    /// Creates a new repository (<paramref name="create"/>) or connects an existing one, then stores it. The
    /// engine call comes first, so a wrong password or an unreachable location leaves nothing behind.
    /// </summary>
    public async Task<DestinationRecord> AddAsync(string name, DestinationKind kind, string location, string password, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            throw new AgentException(AgentError.InvalidArgument, "A destination needs a name of at most 200 characters.");
        }

        if (kind != DestinationKind.FileSystem)
        {
            throw new AgentException(AgentError.InvalidArgument, "Only folder destinations (local disk, USB drive, network share) are supported so far.");
        }

        if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location))
        {
            throw new AgentException(AgentError.InvalidArgument, "The destination must be an absolute folder path.");
        }

        if (string.IsNullOrEmpty(password) || (create && password.Length < MinimumPasswordLength))
        {
            throw new AgentException(AgentError.InvalidArgument, $"The repository password must be at least {MinimumPasswordLength} characters.");
        }

        location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.Destinations.AnyAsync(d => d.Location == location, ct))
        {
            throw new AgentException(AgentError.AlreadyExists, $"{location} is already a destination.");
        }

        var record = new DestinationRecord { Id = Guid.NewGuid(), Name = name.Trim(), Kind = kind, Location = location, ProtectedPassword = [], CreatedAt = time.GetUtcNow() };
        using (var secret = new SecureSecret(password))
        {
            if (create)
            {
                CreateFolder(location);
                await engines.Engine.CreateRepositoryAsync(EngineHost.ToSpec(record), secret, ct);
            }
            else
            {
                await engines.Engine.ConnectRepositoryAsync(EngineHost.ToSpec(record), secret, ct);
            }

            record.ProtectedPassword = protector.Protect(secret);
        }

        db.Destinations.Add(record);
        await db.SaveChangesAsync(ct);
        engines.MarkConnected(record.Id);
        LogAdded(logger, record.Id, create ? "created" : "connected", location);
        return record;
    }

    private static void CreateFolder(string location)
    {
        try
        {
            Directory.CreateDirectory(location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupEngineException(BackupErrorKind.DestinationUnreachable, $"The folder {location} cannot be created: {ex.Message}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Destination {DestinationId} {How} at {Location}")]
    private static partial void LogAdded(ILogger logger, Guid destinationId, string how, string location);
}
