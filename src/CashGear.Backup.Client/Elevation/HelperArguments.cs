using System.Globalization;
using System.Runtime.Versioning;
using CashGear.Backup.Contracts;

namespace CashGear.Backup.Client.Elevation;

/// <summary>
/// The elevated helper's command line: the app's private pipe and the app's process id. Nothing secret goes
/// on it; requests and passwords travel over the pipe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record HelperArguments(string PipeName, int ParentProcessId)
{
    /// <summary>First argument of an elevated helper invocation of the desktop app.</summary>
    public const string Switch = "--cashgear-elevated-helper";

    /// <summary>The agent's pipe. Only debug builds let the app point the helper at another one.</summary>
    public string AgentPipeName { get; init; } = AgentChannel.DefaultPipeName;

    /// <summary>Debug builds only: trust an agent running as the same user (see <see cref="Security.AgentServerTrust.SameUser"/>).</summary>
    public bool TrustSameUserAgent { get; init; }

    public static bool IsHelperInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Switch;

    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { Switch, "--pipe", PipeName, "--parent", ParentProcessId.ToString(CultureInfo.InvariantCulture) };
#if DEBUG
        args.AddRange(["--agent-pipe", AgentPipeName]);
        if (TrustSameUserAgent)
        {
            args.Add("--trust-same-user-agent");
        }
#endif
        return args;
    }

    /// <summary>Null when the arguments are not a well-formed helper invocation.</summary>
    public static HelperArguments? Parse(IReadOnlyList<string> args)
    {
        if (!IsHelperInvocation(args))
        {
            return null;
        }

        string? pipe = null;
        int? parent = null;
        var agentPipe = AgentChannel.DefaultPipeName;
        var trustSameUser = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Count && IsPipeName(args[i + 1]):
                    pipe = args[++i];
                    break;
                case "--parent" when i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0:
                    parent = pid;
                    i++;
                    break;
#if DEBUG
                case "--agent-pipe" when i + 1 < args.Count && IsPipeName(args[i + 1]):
                    agentPipe = args[++i];
                    break;
                case "--trust-same-user-agent":
                    trustSameUser = true;
                    break;
#endif
                default:
                    return null;
            }
        }

        return pipe is null || parent is null ? null : new HelperArguments(pipe, parent.Value) { AgentPipeName = agentPipe, TrustSameUserAgent = trustSameUser };
    }

    internal static bool IsPipeName(string value) =>
        value.Length is > 0 and <= 200 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
