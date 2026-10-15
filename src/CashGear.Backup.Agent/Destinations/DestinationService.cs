using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Destinations;

/// <summary>Adds repositories (new or existing) and remembers them with their DPAPI-protected password.</summary>
internal sealed partial class DestinationService(
    IDbContextFactory<StateDbContext> dbs, EngineHost engines, ISecretProtector protector, Licensing.LicenceService licences, TimeProvider time,
    ILogger<DestinationService> logger)
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
    public async Task<DestinationRecord> AddAsync(DestinationInput input, string password, bool create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
        {
            throw new AgentException(AgentError.InvalidArgument, "A destination needs a name of at most 200 characters.");
        }

        if (string.IsNullOrEmpty(password) || (create && password.Length < MinimumPasswordLength))
        {
            throw new AgentException(AgentError.InvalidArgument, $"The repository password must be at least {MinimumPasswordLength} characters.");
        }

        var location = input.Validate();
        if (input.Kind is DestinationKind.Sftp or DestinationKind.S3)
        {
            await licences.RequireAsync(l => l.CloudDestinations, "Backing up to SFTP or S3", ct);
        }

        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.Destinations.AnyAsync(d => d.Location == location, ct))
        {
            throw new AgentException(AgentError.AlreadyExists, $"{location} is already a destination.");
        }

        var record = new DestinationRecord
        {
            Id = Guid.NewGuid(), Name = input.Name.Trim(), Kind = input.Kind, Location = location, ProtectedPassword = [], CreatedAt = time.GetUtcNow(),
            Settings = input.Kind == DestinationKind.FileSystem ? null : input.Settings.ToJson(),
            ProtectedCredential = input.Credential is { } credential ? protector.Protect(credential) : null,
        };
        using (var secret = new SecureSecret(password))
        {
            await engines.WithSpecAsync(record, async spec =>
            {
                if (create)
                {
                    if (record.Kind is DestinationKind.FileSystem or DestinationKind.NetworkShare)
                    {
                        CreateFolder(location);
                    }

                    await engines.Engine.CreateRepositoryAsync(spec, secret, ct);
                }
                else
                {
                    await engines.Engine.ConnectRepositoryAsync(spec, secret, ct);
                }
            });

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
