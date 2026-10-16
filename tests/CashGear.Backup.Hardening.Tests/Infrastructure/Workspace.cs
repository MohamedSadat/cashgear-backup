using System.Security.Cryptography;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// One test's folders: the agent's data, a local destination, the source and restore targets. Deleted on dispose
/// unless CASHGEAR_KEEP_TEST_WORKSPACE is set (the agent's log is then in data\logs, Kopia's in data\engine\repos).
/// </summary>
internal sealed class Workspace : IDisposable
{
    private Workspace(string root, string? source)
    {
        Root = root;
        Source = source ?? Path.Combine(root, "source-Ünï");
        foreach (var dir in new[] { Data, Destination, Source, Restores })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Destination => Path.Combine(Root, "repo");
    public string Source { get; }
    public string Restores => Path.Combine(Root, "restores");

    /// <summary>A workspace under <paramref name="parent"/> (default %TEMP%\cashgear-hardening); <paramref name="source"/> overrides the source folder.</summary>
    public static Workspace Create(string name, string? parent = null, string? source = null)
    {
        var slug = new string([.. name.Where(char.IsAsciiLetterOrDigit).Take(40)]);
        var root = Path.Combine(parent ?? Path.Combine(Path.GetTempPath(), "cashgear-hardening"), $"{slug}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}");
        return new Workspace(root, source);
    }

    public string RestoreTarget(string name) => Path.Combine(Restores, name);

    public void Dispose()
    {
        if (!HardeningSettings.KeepWorkspace)
        {
            ForceDelete(Root);
        }
    }

    public static void ForceDelete(string dir)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(dir); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A killed process's files can stay locked for a moment.
                Thread.Sleep(300);
            }
        }
    }
}
