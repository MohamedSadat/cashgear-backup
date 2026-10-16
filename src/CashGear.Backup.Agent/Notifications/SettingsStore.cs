using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Notifications;

internal enum SmtpSecurityMode
{
    StartTls,
    Tls,
    None,
}

/// <summary>Where alert emails go. The password is only ever kept as a DPAPI blob.</summary>
internal sealed record EmailConfig
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "";

    /// <summary>0: the usual port for <see cref="Security"/>.</summary>
    public int Port { get; init; }

    public SmtpSecurityMode Security { get; init; } = SmtpSecurityMode.StartTls;
    public string Username { get; init; } = "";
    public byte[]? ProtectedPassword { get; init; }
    public string From { get; init; } = "";

    /// <summary>One or more addresses separated by commas or semicolons.</summary>
    public string To { get; init; } = "";

    [JsonIgnore]
    public int EffectivePort => Port != 0 ? Port : Security switch
    {
        SmtpSecurityMode.Tls => 465,
        SmtpSecurityMode.None => 25,
        _ => 587,
    };

    [JsonIgnore]
    public IReadOnlyList<string> Recipients => To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Alerts, scheduled checks and email. The defaults apply until the first save.</summary>
internal sealed record AgentSettings
{
    public const int DefaultAlertAfterDays = 3;

    public int AlertAfterDays { get; init; } = DefaultAlertAfterDays;

    /// <summary>0: no scheduled checks.</summary>
    public int VerifyEveryDays { get; init; } = 7;

    public double VerifySamplePercent { get; init; } = 5;
    public EmailConfig Email { get; init; } = new();
}

/// <summary>A change of settings. The email password is write-only: null keeps the saved one.</summary>
internal sealed record SettingsUpdate(AgentSettings Settings, SecureSecret? NewPassword, bool ClearPassword);

/// <summary>Settings in state.db's settings table, as JSON; the email password as a DPAPI blob inside it.</summary>
internal sealed class SettingsStore(IDbContextFactory<StateDbContext> dbs, ISecretProtector secrets)
{
    private const string SettingsKey = "Settings";
    private const string EmailedAlertsKey = "EmailedAlerts";
    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<AgentSettings> LoadAsync(CancellationToken ct)
    {
        var value = await ReadAsync(SettingsKey, ct);
        return value is null ? new AgentSettings() : JsonSerializer.Deserialize<AgentSettings>(value, s_json) ?? new AgentSettings();
    }

    public async Task<AgentSettings> SaveAsync(SettingsUpdate update, CancellationToken ct)
    {
        var current = await LoadAsync(ct);
        var input = update.Settings;
        var password = update.ClearPassword ? null
            : update.NewPassword is { } fresh ? secrets.Protect(fresh)
            : current.Email.ProtectedPassword;
        var settings = input with
        {
            AlertAfterDays = input.AlertAfterDays == 0 ? AgentSettings.DefaultAlertAfterDays : input.AlertAfterDays,
            Email = input.Email with { ProtectedPassword = password },
        };

        var errors = Validate(settings);
        if (errors.Count > 0)
        {
            throw new AgentException(AgentError.InvalidArgument, string.Join(" ", errors));
        }

        await WriteAsync(SettingsKey, JsonSerializer.Serialize(settings, s_json), ct);
        return settings;
    }

    /// <summary>The saved email password, for sending; null when none is saved.</summary>
    public SecureSecret? EmailPassword(AgentSettings settings) =>
        settings.Email.ProtectedPassword is { } blob ? secrets.Unprotect(blob) : null;

    public async Task<HashSet<string>> LoadEmailedAlertsAsync(CancellationToken ct) =>
        await ReadAsync(EmailedAlertsKey, ct) is { } value ? JsonSerializer.Deserialize<HashSet<string>>(value) ?? [] : [];

    public Task SaveEmailedAlertsAsync(IEnumerable<string> ids, CancellationToken ct) =>
        WriteAsync(EmailedAlertsKey, JsonSerializer.Serialize(ids.Order(StringComparer.Ordinal)), ct);

    public static List<string> Validate(AgentSettings settings)
    {
        var errors = new List<string>();
        if (settings.AlertAfterDays is < 1 or > 60)
        {
            errors.Add("Alert after 1 to 60 days without a backup.");
        }

        if (settings.VerifyEveryDays is < 0 or > 90)
        {
            errors.Add("Check backups every 1 to 90 days, or 0 for never.");
        }

        if (!double.IsFinite(settings.VerifySamplePercent) || settings.VerifySamplePercent is < 0 or > 100)
        {
            errors.Add("The share of files read back in a check must be 0 to 100 percent.");
        }

        errors.AddRange(ValidateEmail(settings.Email, requireComplete: settings.Email.Enabled));
        return errors;
    }

    /// <param name="requireComplete">Enabled email (or a test email) needs a server and addresses.</param>
    public static List<string> ValidateEmail(EmailConfig email, bool requireComplete)
    {
        var errors = new List<string>();
        if (email.Security == SmtpSecurityMode.None && (email.Username.Length > 0 || email.ProtectedPassword is not null))
        {
            errors.Add("Without encryption the email password would cross the network readable: choose STARTTLS or TLS, or leave the username and password empty.");
        }

        if (email.Port is < 0 or > 65535)
        {
            errors.Add("The mail server port must be 1 to 65535.");
        }

        if (!requireComplete)
        {
            return errors;
        }

        if (email.Host.Length is 0 or > 255 || Uri.CheckHostName(email.Host) == UriHostNameType.Unknown)
        {
            errors.Add("Enter the mail server's name, for example smtp.example.com.");
        }

        if (!IsAddress(email.From))
        {
            errors.Add("Enter the address alerts come from, for example backup@example.com.");
        }

        if (email.Recipients.Count == 0 || !email.Recipients.All(IsAddress))
        {
            errors.Add("Enter one or more addresses to send alerts to, separated by commas.");
        }

        return errors;
    }

    private static bool IsAddress(string value) => value.Length is > 0 and <= 320 && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;
    }

    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var record = await db.Settings.FindAsync([key], ct);
        if (record is null)
        {
            db.Settings.Add(new SettingRecord { Key = key, Value = value });
        }
        else
        {
            record.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }
}
