using System.Text.Json;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Core.Tests;

/// <summary>THIRD-PARTY-NOTICES.txt names everything the agent and the desktop app ship (tools\notices\update-notices.ps1).</summary>
public class NoticesTests
{
    private static readonly string[] s_shippedProjects = ["CashGear.Backup.Agent", "CashGear.Backup.Desktop"];

    [Fact]
    public void Every_shipped_package_and_the_engine_are_in_the_notices()
    {
        var notices = File.ReadAllText(RepoPaths.Combine("THIRD-PARTY-NOTICES.txt"));
        using var pin = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("tools", "kopia", "kopia.pin.json")));
        var missing = new List<string>();

        var engine = $"Kopia {pin.RootElement.GetProperty("version").GetString()} ";
        if (!notices.Contains(engine, StringComparison.Ordinal))
        {
            missing.Add(engine.Trim());
        }

        foreach (var project in s_shippedProjects)
        {
            var assetsFile = RepoPaths.Combine("src", project, "obj", "project.assets.json");
            Assert.SkipUnless(File.Exists(assetsFile), $"{assetsFile} is missing: restore the solution first.");
            using var assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
            foreach (var target in assets.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (library.Value.GetProperty("type").GetString() == "package" && HasRuntimeFiles(library.Value)
                        && !notices.Contains(library.Name.Replace('/', ' ') + Environment.NewLine, StringComparison.Ordinal)
                        && !notices.Contains(library.Name.Replace('/', ' ') + "\n", StringComparison.Ordinal))
                    {
                        missing.Add(library.Name);
                    }
                }
            }
        }

        Assert.True(missing.Count == 0,
            "THIRD-PARTY-NOTICES.txt is out of date; run tools\\notices\\update-notices.ps1. Missing: " + string.Join(", ", missing.Distinct()));
    }

    private static bool HasRuntimeFiles(JsonElement library) =>
        new[] { "runtime", "runtimeTargets", "native" }.Any(kind =>
            library.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)));
}
