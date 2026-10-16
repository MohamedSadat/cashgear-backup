namespace CashGear.Backup.Core;

/// <summary>What the host tells the engine when it creates it.</summary>
public sealed record BackupEngineSettings
{
    /// <summary>Absolute path of the folder the engine keeps its own state in (configs, caches, logs).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// Stable name this machine's snapshots are recorded under. It must survive a machine rename, so it is
    /// generated once and stored, never taken from the computer name.
    /// </summary>
    public required string MachineIdentity { get; init; }

    /// <summary>The engine executable; null means the one bundled next to the application.</summary>
    public string? ExecutablePath { get; init; }
}

/// <summary>Creates the product's backup engine. The host registers one implementation and never names it.</summary>
public interface IBackupEngineFactory
{
    IBackupEngine Create(BackupEngineSettings settings);
}
