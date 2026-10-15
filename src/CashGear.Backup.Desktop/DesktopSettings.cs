using System.IO;
using CashGear.Backup.Contracts;

namespace CashGear.Backup.Desktop;

/// <summary>How this app finds the agent. Shipped builds always use the installed service's pipe.</summary>
internal sealed record DesktopSettings(string AgentPipeName, bool TrustSameUserAgent)
{
    /// <summary>WebView2's profile: the default (next to the exe) is not writable under Program Files.</summary>
    public static string WebViewDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CashGear", "Backup", "WebView2");

    public static DesktopSettings FromEnvironment()
    {
#if DEBUG
        // Development against an agent run as a console app (CLAUDE.md, "Build and test"): it runs as the developer,
        // not as the service, so the pipe check must be told to accept that.
        var pipe = Environment.GetEnvironmentVariable("CASHGEAR_AGENT_PIPE");
        return new(
            string.IsNullOrWhiteSpace(pipe) || !pipe.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? AgentChannel.DefaultPipeName : pipe,
            Environment.GetEnvironmentVariable("CASHGEAR_TRUST_DEV_AGENT") == "1");
#else
        return new(AgentChannel.DefaultPipeName, TrustSameUserAgent: false);
#endif
    }
}
