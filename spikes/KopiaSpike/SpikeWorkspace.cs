namespace KopiaSpike;

/// <summary>
/// Per-run folders. Everything Kopia touches lives under <see cref="Root"/> in %TEMP%; HTTP captures,
/// server logs and the summary go to <see cref="Artifacts"/> so they survive cleanup.
/// </summary>
public sealed class SpikeWorkspace : IDisposable
{
    private SpikeWorkspace(string root, string artifacts)
    {
        Root = root;
        Artifacts = artifacts;
    }

    public string Root { get; }
    public string Artifacts { get; }
    public string Repo => Path.Combine(Root, "repo");
    public string ConfigFile => Path.Combine(Root, "config", "repository.config");
    public string Cache => Path.Combine(Root, "cache");
    public string KopiaLogs => Path.Combine(Root, "kopia-logs");
    public string Tls => Path.Combine(Root, "tls");
    public string Html => Path.Combine(Root, "html");

    /// <summary>Non-ASCII on purpose: source paths must survive URLs, JSON and Kopia's manifests.</summary>
    public string Source => Path.Combine(Root, "source-Ünï");

    public string Restores => Path.Combine(Root, "restores");
    public string Http => Path.Combine(Artifacts, "http");

    /// <summary>Keep the %TEMP% folders after the run (set on failure so it can be inspected).</summary>
    public bool Keep { get; set; }

    public static SpikeWorkspace Create(string runId, string artifactsRoot)
    {
        var ws = new SpikeWorkspace(
            Path.Combine(Path.GetTempPath(), "cashgear-spike", runId),
            Path.Combine(artifactsRoot, runId));

        foreach (var dir in new[] { ws.Repo, Path.GetDirectoryName(ws.ConfigFile)!, ws.Cache, ws.KopiaLogs, ws.Tls, ws.Html, ws.Source, ws.Restores, ws.Http })
        {
            Directory.CreateDirectory(dir);
        }

        // Kopia serves this instead of its embedded web UI; GET / is only used to obtain the CSRF token.
        File.WriteAllText(Path.Combine(ws.Html, "index.html"), "<!doctype html><html><head><title>CashGear</title></head><body></body></html>");
        return ws;
    }

    public string RestoreTarget(string name) => Path.Combine(Restores, name);

    public void Dispose()
    {
        if (!Keep)
        {
            ForceDelete(Root);
        }
    }

    public static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }
}
