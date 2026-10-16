using System.Reflection;

namespace CashGear.Backup.Agent;

public static class AgentInfo
{
    /// <summary>Windows service name the installer registers.</summary>
    public const string ServiceName = Contracts.AgentChannel.ServiceName;

    /// <summary>Event Log source; the installer creates it (the service cannot without admin rights at run time).</summary>
    public const string EventLogSource = "CashGear Backup";

    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
