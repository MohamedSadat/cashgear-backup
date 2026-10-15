using System.Reflection;

namespace CashGear.Backup.Testing;

/// <summary>Repository paths for tests, from metadata stamped in by tests/Directory.Build.props.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = ReadRoot();

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string ReadRoot()
    {
        var root = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CashGear.RepoRoot")?.Value;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException(
                "Assembly metadata 'CashGear.RepoRoot' is missing; link this file only into projects under tests/ or spikes/.");
        }

        return Path.GetFullPath(root);
    }
}
