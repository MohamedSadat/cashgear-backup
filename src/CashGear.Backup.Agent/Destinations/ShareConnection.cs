using System.Runtime.InteropServices;
using CashGear.Backup.Core;

namespace CashGear.Backup.Agent.Destinations;

/// <summary>
/// Opens an SMB share with its own account for the agent's logon session (LocalSystem cannot use the user's
/// mapped drives or saved credentials). A deviceless, temporary connection: nothing is mapped to a drive letter
/// and nothing is remembered across a restart; the agent opens it again before each use.
/// </summary>
internal static partial class ShareConnection
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 0x4;

    /// <summary>\\server\share of a UNC path, or null when the path is not one.</summary>
    public static string? ShareRoot(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : null;
    }

    /// <summary>Connects (or confirms the connection to) the share; failures are <see cref="BackupEngineException"/>s.</summary>
    public static void Connect(string path, string username, SecureSecret password)
    {
        var root = ShareRoot(path) ?? throw new BackupEngineException(BackupErrorKind.InvalidRequest, $"{path} is not a network share path.");
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = root };
        var result = WNetAddConnection2W(ref resource, password.Reveal(), username, ConnectTemporary);
        if (Classify(result) is { } failure)
        {
            throw new BackupEngineException(failure.Kind, $"The network share {root} could not be opened as {username}: {failure.Message}");
        }
    }

    /// <summary>Null when the share is open (newly, or already with these credentials).</summary>
    internal static (BackupErrorKind Kind, string Message)? Classify(int error) => error switch
    {
        0 or 85 => null,
        5 or 86 or 1326 or 1327 or 1330 or 1331 => (BackupErrorKind.DestinationAccessDenied, "the account or password was not accepted."),
        1219 => (BackupErrorKind.DestinationAccessDenied, "this PC is already connected to that server with another account."),
        53 or 67 or 1203 or 1222 or 1231 or 2250 => (BackupErrorKind.DestinationUnreachable, "the server or share cannot be reached."),
        _ => (BackupErrorKind.DestinationUnreachable, $"Windows error {error}."),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(ref NetResource resource, string password, string username, int flags);
}
