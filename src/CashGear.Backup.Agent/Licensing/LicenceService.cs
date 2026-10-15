using System.Text.Json;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Licensing;

internal enum LicenceState
{
    Free,
    Active,

    /// <summary>The server has not been reached for a while; everything still works until the grace period ends.</summary>
    OfflineGrace,

    /// <summary>Ended, revoked, or the server was not reached for the whole grace period: new backups are paused.</summary>
    Lapsed,
}

/// <summary>The licence in force and what it allows.</summary>
internal sealed record LicenceInfo(
    LicenceEdition Edition,
    LicenceState State,
    string? KeyHint,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceUntil,
    DateTimeOffset? LastCheckedAt,
    string Message)
{
    /// <summary>0: unlimited.</summary>
    public int MaxJobs => Edition == LicenceEdition.Free ? 1 : 0;

    public bool CloudDestinations => Edition != LicenceEdition.Free;
    public bool EmailAlerts => Edition != LicenceEdition.Free;

    /// <summary>A lapsed licence pauses new backups. Restores never depend on the licence.</summary>
    public bool BackupsAllowed => State != LicenceState.Lapsed;
}

/// <summary>
/// Editions and licence keys. A key is activated with the licence server, which returns a signed token for this PC;
/// the token is refreshed daily. Without the server the licence keeps working for <see cref="OfflineGrace"/> after
/// the last token was issued, so backups never stop because the server is down. A lapsed licence pauses new
/// backups but never restores. Without a valid token: the Free edition (one job, folders and network shares, no
/// email alerts).
/// </summary>
internal sealed partial class LicenceService(
    IDbContextFactory<StateDbContext> dbs, AgentOptions options, AgentState state, ILicenceServer server, TimeProvider time, ILogger<LicenceService> logger)
{
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);

    /// <summary>After this long without reaching the server, the status says so (still fully working).</summary>
    public static readonly TimeSpan OfflineWarning = TimeSpan.FromDays(7);

    private const string SettingKey = "Licence";

    public async Task<LicenceInfo> GetAsync(CancellationToken ct)
    {
        if (!options.LicenceEnforcement)
        {
            return new LicenceInfo(LicenceEdition.Business, LicenceState.Active, null, null, null, null, "Licence checks are off on this installation (development).");
        }

        var stored = await LoadAsync(ct);
        if (stored.Token is null)
        {
            return Free(stored.LastError ?? "");
        }

        var payload = LicenceToken.Verify(stored.Token, options.LicencePublicKeys);
        if (payload is null || payload.Machine != state.MachineIdentity)
        {
            return Free("The saved licence could not be verified for this PC; activate the key again.");
        }

        var now = time.GetUtcNow();
        var graceUntil = payload.IssuedAt + OfflineGrace;
        if (stored.Revoked)
        {
            return Info(payload, stored, LicenceState.Lapsed, graceUntil, stored.LastError ?? "The licence is no longer valid. Backups are paused; restoring still works.");
        }

        if (payload.ExpiresAt is { } expires && now > expires)
        {
            return Info(payload, stored, LicenceState.Lapsed, graceUntil, $"The licence ended on {expires:d MMM yyyy}. Backups are paused; restoring still works.");
        }

        if (now > graceUntil)
        {
            return Info(payload, stored, LicenceState.Lapsed, graceUntil,
                "The licence server has not been reached for 30 days. Backups are paused until it is; restoring still works.");
        }

        return now - payload.IssuedAt > OfflineWarning
            ? Info(payload, stored, LicenceState.OfflineGrace, graceUntil, $"The licence server has not been reached since {payload.IssuedAt:d MMM yyyy}. Everything works until {graceUntil:d MMM yyyy}.")
            : Info(payload, stored, LicenceState.Active, graceUntil, "");
    }

    public async Task<LicenceInfo> ActivateAsync(string key, CancellationToken ct)
    {
        key = key.Trim();
        if (key.Length is 0 or > 200)
        {
            throw new AgentException(AgentError.InvalidArgument, "Enter the licence key.");
        }

        string token;
        try
        {
            token = await server.ActivateAsync(key, state.MachineIdentity, AgentInfo.Version, ct);
        }
        catch (LicenceRejectedException ex)
        {
            throw new AgentException(AgentError.InvalidArgument, "The licence server did not accept the key: " + ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new AgentException(AgentError.FailedPrecondition, "The licence server could not be reached: " + ex.Message);
        }

        var payload = LicenceToken.Verify(token, options.LicencePublicKeys);
        if (payload is null || payload.Machine != state.MachineIdentity)
        {
            throw new AgentException(AgentError.InvalidArgument, "The licence server's answer could not be verified, so the licence was not saved.");
        }

        await SaveAsync(new StoredLicence { Token = token, KeyHint = Hint(key), LastCheckedAt = time.GetUtcNow() }, ct);
        LogActivated(logger, payload.LicenceId, payload.Edition);
        return await GetAsync(ct);
    }

    public async Task<LicenceInfo> RemoveAsync(CancellationToken ct)
    {
        await SaveAsync(new StoredLicence(), ct);
        LogRemoved(logger);
        return await GetAsync(ct);
    }

    /// <summary>Once a day: a fresh token. Failures keep the current one (the grace period runs on).</summary>
    public async Task RefreshIfDueAsync(CancellationToken ct)
    {
        var stored = await LoadAsync(ct);
        if (stored.Token is null || stored.Revoked || LicenceToken.Verify(stored.Token, options.LicencePublicKeys) is not { } payload)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (stored.LastAttemptAt is { } attempted && now - attempted < TimeSpan.FromHours(1)
            || stored.LastCheckedAt is { } checkedAt && now - checkedAt < RefreshEvery)
        {
            return;
        }

        stored = stored with { LastAttemptAt = now };
        try
        {
            var token = await server.RefreshAsync(payload.LicenceId, state.MachineIdentity, ct);
            if (LicenceToken.Verify(token, options.LicencePublicKeys) is { } fresh && fresh.Machine == state.MachineIdentity)
            {
                stored = stored with { Token = token, LastCheckedAt = now, LastError = null };
            }
            else
            {
                stored = stored with { LastError = "The licence server's answer could not be verified." };
            }
        }
        catch (LicenceRejectedException ex)
        {
            stored = stored with { Revoked = ex.Revoked, LastError = ex.Message };
            LogRefreshRefused(logger, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            stored = stored with { LastError = "The licence server could not be reached: " + ex.Message };
        }

        await SaveAsync(stored, ct);
    }

    /// <summary>A key left by a silent install (licence.ini in the data folder, written by the MSI) is activated once.</summary>
    public async Task ActivatePreseededKeyAsync(CancellationToken ct)
    {
        if (!File.Exists(options.LicenceKeyFile) || (await LoadAsync(ct)).Token is not null)
        {
            return;
        }

        var key = (await File.ReadAllLinesAsync(options.LicenceKeyFile, ct))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("Key=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            await ActivateAsync(key, ct);
            File.Delete(options.LicenceKeyFile);
        }
        catch (AgentException ex)
        {
            LogPreseededFailed(logger, ex.Message);
        }
    }

    /// <summary>Throws <see cref="AgentError.LicenceRequired"/> unless the edition in force allows the feature.</summary>
    public async Task RequireAsync(Func<LicenceInfo, bool> allowed, string what, CancellationToken ct)
    {
        var licence = await GetAsync(ct);
        if (!allowed(licence))
        {
            throw new AgentException(AgentError.LicenceRequired, $"{what} needs a Pro licence. Activate one in Settings.");
        }
    }

    /// <summary>Why a backup of this job may not run now; null when it may.</summary>
    public async Task<string?> BackupBlockedAsync(JobRecord job, DestinationRecord destination, CancellationToken ct)
    {
        var licence = await GetAsync(ct);
        if (!licence.BackupsAllowed)
        {
            return licence.Message;
        }

        if (!licence.CloudDestinations && destination.Kind is DestinationKind.Sftp or DestinationKind.S3)
        {
            return "Backing up to SFTP or S3 needs a Pro licence; this PC has the Free edition. Restoring still works.";
        }

        if (licence.MaxJobs > 0)
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var allowed = await db.Jobs.AsNoTracking().OrderBy(j => j.CreatedAt).Select(j => j.Id).Take(licence.MaxJobs).ToListAsync(ct);
            if (!allowed.Contains(job.Id))
            {
                return "The Free edition backs up one job; activate a Pro licence to back up this one too. Restoring still works.";
            }
        }

        return null;
    }

    private static LicenceInfo Free(string message) => new(LicenceEdition.Free, LicenceState.Free, null, null, null, null, message);

    private static LicenceInfo Info(LicencePayload payload, StoredLicence stored, LicenceState licenceState, DateTimeOffset graceUntil, string message) =>
        new(payload.Edition == LicenceEdition.Free ? LicenceEdition.Pro : payload.Edition, licenceState, stored.KeyHint, payload.ExpiresAt, graceUntil,
            stored.LastCheckedAt, message);

    private static string Hint(string key)
    {
        var characters = key.Where(char.IsAsciiLetterOrDigit).ToArray();
        return characters.Length >= 4 ? new string(characters[^4..]) : "";
    }

    private async Task<StoredLicence> LoadAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var value = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey, ct))?.Value;
        return value is null ? new StoredLicence() : JsonSerializer.Deserialize<StoredLicence>(value) ?? new StoredLicence();
    }

    private async Task SaveAsync(StoredLicence licence, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var record = await db.Settings.FindAsync([SettingKey], ct);
        var value = JsonSerializer.Serialize(licence);
        if (record is null)
        {
            db.Settings.Add(new SettingRecord { Key = SettingKey, Value = value });
        }
        else
        {
            record.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The signed token (verified each time it is used), never the key itself.</summary>
    private sealed record StoredLicence
    {
        public string? Token { get; init; }
        public string? KeyHint { get; init; }
        public DateTimeOffset? LastCheckedAt { get; init; }
        public DateTimeOffset? LastAttemptAt { get; init; }
        public bool Revoked { get; init; }
        public string? LastError { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Licence {LicenceId} ({Edition}) activated")]
    private static partial void LogActivated(ILogger logger, string licenceId, LicenceEdition edition);

    [LoggerMessage(Level = LogLevel.Information, Message = "Licence removed; Free edition")]
    private static partial void LogRemoved(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The licence server refused the refresh: {Reason}")]
    private static partial void LogRefreshRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The licence key from the installer could not be activated: {Reason}")]
    private static partial void LogPreseededFailed(ILogger logger, string reason);
}
