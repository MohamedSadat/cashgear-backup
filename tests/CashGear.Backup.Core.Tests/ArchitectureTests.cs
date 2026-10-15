using CashGear.Backup.Testing;

namespace CashGear.Backup.Core.Tests;

/// <summary>Guards the rules in CLAUDE.md that the compiler cannot enforce.</summary>
public class ArchitectureTests
{
    private const string EngineProject = "CashGear.Backup.Engine.Kopia";
    private static readonly string[] s_sourceExtensions = [".cs", ".razor", ".csproj", ".xaml", ".json", ".proto", ".props", ".targets"];

    [Fact]
    public void Only_the_engine_project_mentions_Kopia()
    {
        var engineDir = Path.Combine("src", EngineProject) + Path.DirectorySeparatorChar;
        var violations = new List<string>();
        foreach (var file in SourceFiles())
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            if (relative.StartsWith(engineDir, StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("kopia", StringComparison.OrdinalIgnoreCase) && !IsEngineProjectReference(lines[i]))
                {
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Only src/CashGear.Backup.Engine.Kopia may know about Kopia (other projects may only reference it):"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Product_code_never_references_spikes()
    {
        var violations = SourceFiles()
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("spikes", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f))
            .ToList();

        Assert.True(violations.Count == 0, "src/ must not reference spikes/: " + string.Join(", ", violations));
    }

    private static bool IsEngineProjectReference(string line) =>
        line.Contains("<ProjectReference", StringComparison.Ordinal)
        && line.Contains($@"{EngineProject}\{EngineProject}.csproj", StringComparison.Ordinal);

    private static IEnumerable<string> SourceFiles()
    {
        var src = RepoPaths.Combine("src");
        return Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => s_sourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f =>
            {
                var parts = Path.GetRelativePath(src, f).Split(Path.DirectorySeparatorChar);
                return !parts.Contains("bin") && !parts.Contains("obj");
            });
    }
}
