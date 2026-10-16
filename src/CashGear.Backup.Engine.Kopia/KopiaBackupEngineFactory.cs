using CashGear.Backup.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia;

/// <summary>Creates <see cref="KopiaBackupEngine"/> instances with the kopia.exe bundled next to the application.</summary>
public sealed class KopiaBackupEngineFactory(ILoggerFactory loggerFactory) : IBackupEngineFactory
{
    /// <summary>Where the build (and later the installer) puts the pinned kopia.exe.</summary>
    public static string BundledExecutablePath => Path.Combine(AppContext.BaseDirectory, "kopia.exe");

    public IBackupEngine Create(BackupEngineSettings settings) => new KopiaBackupEngine(
        new KopiaEngineOptions
        {
            KopiaExePath = settings.ExecutablePath ?? BundledExecutablePath,
            DataDirectory = settings.DataDirectory,
            ClientHostName = settings.MachineIdentity,
        },
        loggerFactory);
}
