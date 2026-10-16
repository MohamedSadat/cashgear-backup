using CashGear.Backup.Contracts;
using CashGear.Backup.Data;

namespace CashGear.Backup.Agent;

/// <summary>The "Agent" section of appsettings.json. The defaults are the installed product's.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>state.db, engine data and logs. The installer ACLs it to SYSTEM and Administrators only.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CashGear", "Backup");

    public string PipeName { get; set; } = AgentChannel.DefaultPipeName;

    /// <summary>The engine executable; null means the one installed next to the agent.</summary>
    public string? EngineExecutablePath { get; set; }

    /// <summary>
    /// Lets callers that are not elevated administrators use the "Manage" calls (create destinations and
    /// jobs, restore). Off by default: those calls make the agent, running as LocalSystem, read and write
    /// folders the caller names. Tests and development turn it on.
    /// </summary>
    public bool AllowNonElevatedManage { get; set; }

    /// <summary>
    /// How often the agent starts due checks of backups and emails new alerts. Infinite turns the background loop
    /// off (tests run it by hand).
    /// </summary>
    public TimeSpan HousekeepingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The licence server (activation, daily refresh), https://. Empty: none configured, so no key can be activated
    /// and the Free edition applies.
    /// </summary>
    public string LicenceServerUrl { get; set; } = "";

    /// <summary>Public keys (SubjectPublicKeyInfo, base64) whose signature makes a licence token valid.</summary>
    public List<string> LicencePublicKeys { get; set; } = [];

    /// <summary>Off only for tests and development: every feature on, nothing checked.</summary>
    public bool LicenceEnforcement { get; set; } = true;

    /// <summary>The signed update feed (https://). Empty: no automatic updates.</summary>
    public string UpdateManifestUrl { get; set; } = "";

    /// <summary>Public keys (SubjectPublicKeyInfo, base64) whose signature makes the update feed valid.</summary>
    public List<string> UpdatePublicKeys { get; set; } = [];

    /// <summary>SHA-256 thumbprints of the certificates allowed to sign update MSIs (Authenticode).</summary>
    public List<string> UpdatePublisherThumbprints { get; set; } = [];

    /// <summary>Install verified updates by themselves once no run is active.</summary>
    public bool AutomaticUpdates { get; set; } = true;

    /// <summary>Also log to the console when not running as a service.</summary>
    public bool WriteToConsole { get; set; } = true;

    internal string StateDbPath => Path.Combine(DataDirectory, StateDb.FileName);
    internal string EngineDirectory => Path.Combine(DataDirectory, "engine");
    internal string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>A licence key left by a silent install ([Licence] Key=...), activated once at start.</summary>
    internal string LicenceKeyFile => Path.Combine(DataDirectory, "licence.ini");

    internal string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    internal IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DataDirectory) || !Path.IsPathFullyQualified(DataDirectory))
        {
            errors.Add("Agent:DataDirectory must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Contains('\\', StringComparison.Ordinal) || PipeName.Contains('/', StringComparison.Ordinal))
        {
            errors.Add("Agent:PipeName must be a plain pipe name.");
        }

        if (EngineExecutablePath is not null && !Path.IsPathFullyQualified(EngineExecutablePath))
        {
            errors.Add("Agent:EngineExecutablePath must be an absolute path.");
        }

        return errors;
    }
}
