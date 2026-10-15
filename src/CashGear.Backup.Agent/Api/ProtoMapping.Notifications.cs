using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Core;
using Google.Protobuf.WellKnownTypes;
using Proto = CashGear.Backup.Contracts.V1;

namespace CashGear.Backup.Agent.Api;

internal static partial class ProtoMapping
{
    public static Proto.Alert ToProto(AlertInfo alert) => new()
    {
        Id = alert.Id,
        Kind = alert.Kind switch
        {
            AlertKind.VerifyFailed => Proto.AlertKind.VerifyFailed,
            AlertKind.BackupFailed => Proto.AlertKind.BackupFailed,
            AlertKind.BackupSkippedFiles => Proto.AlertKind.BackupSkippedFiles,
            _ => Proto.AlertKind.NoRecentBackup,
        },
        JobId = alert.JobId.ToString(),
        JobName = alert.JobName,
        Message = alert.Message,
        Since = Timestamp.FromDateTimeOffset(alert.Since),
    };

    /// <summary>The email password is never sent back; only whether one is saved.</summary>
    public static Proto.Settings ToProto(AgentSettings settings) => new()
    {
        AlertAfterDays = settings.AlertAfterDays,
        VerifyEveryDays = settings.VerifyEveryDays,
        VerifySamplePercent = settings.VerifySamplePercent,
        Email = new Proto.EmailSettings
        {
            Enabled = settings.Email.Enabled,
            SmtpHost = settings.Email.Host,
            SmtpPort = settings.Email.Port,
            Security = settings.Email.Security switch
            {
                SmtpSecurityMode.Tls => Proto.SmtpSecurity.Tls,
                SmtpSecurityMode.None => Proto.SmtpSecurity.None,
                _ => Proto.SmtpSecurity.Starttls,
            },
            Username = settings.Email.Username,
            PasswordSaved = settings.Email.ProtectedPassword is not null,
            FromAddress = settings.Email.From,
            ToAddresses = settings.Email.To,
        },
    };

    public static SettingsUpdate FromProto(Proto.Settings? settings)
    {
        if (settings is null)
        {
            throw new AgentException(AgentError.InvalidArgument, "The settings are missing.");
        }

        var (email, password) = FromProto(settings.Email);
        return new SettingsUpdate(
            new AgentSettings
            {
                AlertAfterDays = settings.AlertAfterDays,
                VerifyEveryDays = settings.VerifyEveryDays,
                VerifySamplePercent = settings.VerifySamplePercent,
                Email = email,
            },
            password,
            settings.Email?.ClearPassword == true);
    }

    /// <summary>The email settings, and the password when one was given (the caller disposes it).</summary>
    public static (EmailConfig Email, SecureSecret? Password) FromProto(Proto.EmailSettings? email)
    {
        if (email is null)
        {
            return (new EmailConfig(), null);
        }

        var config = new EmailConfig
        {
            Enabled = email.Enabled,
            Host = email.SmtpHost.Trim(),
            Port = email.SmtpPort,
            Security = email.Security switch
            {
                Proto.SmtpSecurity.Tls => SmtpSecurityMode.Tls,
                Proto.SmtpSecurity.None => SmtpSecurityMode.None,
                _ => SmtpSecurityMode.StartTls,
            },
            Username = email.Username.Trim(),
            From = email.FromAddress.Trim(),
            To = email.ToAddresses.Trim(),
        };
        return (config, string.IsNullOrEmpty(email.Password) ? null : new SecureSecret(email.Password));
    }
}
