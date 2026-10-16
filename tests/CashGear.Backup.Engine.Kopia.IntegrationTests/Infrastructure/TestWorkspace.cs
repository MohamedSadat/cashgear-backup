using System.Security.Cryptography;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

/// <summary>
/// Per-test folders under %TEMP%\cashgear-tests: engine data, the repository (destination), the source and
/// restore targets. Deleted on dispose unless CASHGEAR_KEEP_TEST_WORKSPACE is set.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root) => Root = root;

    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Destination => Path.Combine(Root, "repo");

    /// <summary>Non-ASCII on purpose: source paths must survive URLs, JSON and Kopia's manifests.</summary>
    public string Source => Path.Combine(Root, "source-Ünï");

    public string Restores => Path.Combine(Root, "restores");

    public static TestWorkspace Create(string name)
    {
        var slug = new string([.. name.Where(char.IsAsciiLetterOrDigit).Take(40)]);
        var ws = new TestWorkspace(Path.Combine(Path.GetTempPath(), "cashgear-tests", $"{slug}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}"));
        foreach (var dir in new[] { ws.Data, ws.Destination, ws.Source, ws.Restores })
        {
            Directory.CreateDirectory(dir);
        }

        return ws;
    }

    public string RestoreTarget(string name) => Path.Combine(Restores, name);

    public void Dispose()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_WORKSPACE")))
        {
            ForceDelete(Root);
        }
    }

    public static void ForceDelete(string dir)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(dir); attempt++)
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
                // Kopia's log or cache files can stay locked for a moment after the process exits.
                Thread.Sleep(200);
            }
        }
    }
}
