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

    /// <summary>Also log to the console when not running as a service.</summary>
    public bool WriteToConsole { get; set; } = true;

    internal string StateDbPath => Path.Combine(DataDirectory, StateDb.FileName);
    internal string EngineDirectory => Path.Combine(DataDirectory, "engine");
    internal string LogDirectory => Path.Combine(DataDirectory, "logs");

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
