namespace CashGear.Backup.Engine.Kopia;

/// <summary>Settings for <see cref="KopiaBackupEngine"/>.</summary>
public sealed record KopiaEngineOptions
{
    /// <summary>
    /// Absolute path of the bundled kopia.exe. Before every start it is checked against the pin compiled into
    /// this assembly (tools/kopia/kopia.pin.json); nothing else is ever run, and PATH is never searched.
    /// </summary>
    public required string KopiaExePath { get; init; }

    /// <summary>
    /// Absolute path of the engine's data folder: per-repository Kopia config, cache, logs and the transient
    /// TLS files. In production it must be ACL'd to SYSTEM and Administrators only.
    /// </summary>
    public required string DataDirectory { get; init; }

    /// <summary>
    /// Host name snapshots are recorded under. Sources are keyed by <c>user@host:path</c>, so this must stay the
    /// same when the machine is renamed (use an id from state.db, not <see cref="Environment.MachineName"/>).
    /// </summary>
    public required string ClientHostName { get; init; }

    public string ClientUserName { get; init; } = "cashgear";

    public TimeSpan ServerStartTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RestartBackoffInitial { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RestartBackoffMax { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a running backup or restore is polled for progress.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long Kopia may take to show the task for a backup it was asked to start.</summary>
    public TimeSpan TaskDiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long create/connect wait for the repository to report itself connected.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long Kopia may keep failing to write to the destination, once it has given up on a write, before the
    /// engine ends the backup itself as Failed. Kopia never fails a snapshot for that: it skips the files and goes on
    /// (<c>Runs/DestinationWatch</c>). Longer than an outage Kopia rides out with warnings (60 s was).
    /// </summary>
    public TimeSpan DestinationLossWindow { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>A pause this long in Kopia's storage errors means writes go through again; the next error starts the count afresh.</summary>
    public TimeSpan DestinationQuietPeriod { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a running backup's task log is read for storage errors.</summary>
    public TimeSpan TaskLogInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a new backup of a folder waits for the previous one to stop in Kopia when the engine ended that one
    /// itself (lost destination); after that the new one is refused with RunAlreadyActive.
    /// </summary>
    public TimeSpan StoppingRunWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Finished runs kept in memory so late watchers still get their result.</summary>
    public int FinishedRunsKept { get; init; } = 100;

    internal IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(KopiaExePath) || !Path.IsPathFullyQualified(KopiaExePath))
        {
            errors.Add("KopiaExePath must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(DataDirectory) || !Path.IsPathFullyQualified(DataDirectory))
        {
            errors.Add("DataDirectory must be an absolute path.");
        }

        if (!IsIdentityPart(ClientHostName) || !IsIdentityPart(ClientUserName))
        {
            errors.Add("ClientHostName and ClientUserName must be 1-63 characters of letters, digits, '.', '_' or '-'.");
        }

        if (PollInterval <= TimeSpan.Zero || HealthCheckInterval <= TimeSpan.Zero || FinishedRunsKept < 1)
        {
            errors.Add("Intervals must be positive and FinishedRunsKept at least 1.");
        }

        if (DestinationLossWindow <= TimeSpan.Zero || DestinationQuietPeriod <= TimeSpan.Zero || TaskLogInterval <= TimeSpan.Zero || StoppingRunWait < TimeSpan.Zero)
        {
            errors.Add("DestinationLossWindow, DestinationQuietPeriod and TaskLogInterval must be positive, StoppingRunWait not negative.");
        }

        return errors;
    }

    private static bool IsIdentityPart(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 63 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
