using System.Text;
using System.Text.Json;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Core.Tests;

/// <summary>THIRD-PARTY-NOTICES.txt names everything the agent and the desktop app ship (tools\notices\update-notices.ps1).</summary>
public class NoticesTests
{
    private const string CollectCommand = @"tools\notices\collect-kopia-go-modules.ps1";
    private const string UpdateCommand = @"tools\notices\update-notices.ps1";
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

    [Fact]
    public void Go_module_data_was_collected_for_the_pinned_kopia()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("tools", "kopia", "kopia.pin.json")));
        using var data = ReadGoModuleData();
        var pinVersion = pin.RootElement.GetProperty("version").GetString();
        var dataVersion = data.RootElement.GetProperty("kopiaVersion").GetString();
        Assert.True(
            dataVersion == pinVersion
                && data.RootElement.GetProperty("kopiaExeSha256").GetString() == pin.RootElement.GetProperty("exe").GetProperty("sha256").GetString(),
            $"tools/notices/kopia-go-modules.json was collected for Kopia {dataVersion}, but the pin is Kopia {pinVersion}. " +
            $"Run {CollectCommand}, then {UpdateCommand}.");

        var entries = GoEntries(data).ToList();
        Assert.True(entries.Count > 1, $"tools/notices/kopia-go-modules.json lists no Go modules; run {CollectCommand}.");
        var problems = new List<string>();
        foreach (var (label, folder, item) in entries)
        {
            if (string.IsNullOrEmpty(item.GetProperty("license").GetString()))
            {
                problems.Add($"{label} has no licence");
            }

            var files = item.GetProperty("files").EnumerateArray().ToList();
            if (!files.Any(f => !f.TryGetProperty("notice", out _) && !f.GetProperty("path").GetString()!.Contains('/')))
            {
                problems.Add($"{label} has no licence file at the module root");
            }

            foreach (var file in files.Where(f => !f.TryGetProperty("standardApache", out _)))
            {
                var path = RepoPaths.Combine(["tools", "notices", "kopia-go-licenses", .. $"{folder}/{file.GetProperty("path").GetString()}".Split('/')]);
                if (!File.Exists(path))
                {
                    problems.Add($"{path} is missing");
                }
            }
        }

        Assert.True(problems.Count == 0, $"tools/notices/kopia-go-modules.json is incomplete; run {CollectCommand}: " + string.Join("; ", problems));
    }

    [Fact]
    public void Every_Go_module_in_kopia_is_in_the_notices()
    {
        var notices = File.ReadAllText(RepoPaths.Combine("THIRD-PARTY-NOTICES.txt")).ReplaceLineEndings("\n");
        var modules = Section(notices, "3. Go modules in kopia.exe", "4. Licence texts");
        var texts = Section(notices, "5. Go module licence and notice files", null);
        using var data = ReadGoModuleData();
        var missing = new List<string>();

        foreach (var (label, _, item) in GoEntries(data))
        {
            var title = item.TryGetProperty("path", out _) ? label : $"{label} standard library and runtime";
            if (!modules.Contains($"\n{title}\n  Licence: {item.GetProperty("license").GetString()}\n", StringComparison.Ordinal))
            {
                missing.Add(title);
            }

            foreach (var file in item.GetProperty("files").EnumerateArray().Where(f => !f.TryGetProperty("standardApache", out _)))
            {
                var fileLabel = $"{label}: {file.GetProperty("path").GetString()}";
                if (!texts.Contains($"\n{fileLabel}\n", StringComparison.Ordinal))
                {
                    missing.Add(fileLabel);
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"Sections 3 and 5 of THIRD-PARTY-NOTICES.txt are out of date; run {UpdateCommand}. Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Go_module_data_matches_the_build_info_in_kopia_exe()
    {
        var (goVersion, built) = ReadGoBuildInfo(KopiaTestBinary.ExePath);
        using var data = ReadGoModuleData();
        var recorded = data.RootElement.GetProperty("modules").EnumerateArray()
            .Select(m => $"{m.GetProperty("path").GetString()} {m.GetProperty("version").GetString()} {m.GetProperty("sum").GetString()}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            goVersion == data.RootElement.GetProperty("go").GetProperty("version").GetString() && built.SetEquals(recorded),
            $"tools/notices/kopia-go-modules.json does not match the build info in kopia.exe ({goVersion}); run {CollectCommand}. " +
            $"Not recorded: {string.Join(", ", built.Except(recorded))}. Not in kopia.exe: {string.Join(", ", recorded.Except(built))}.");
    }

    private static bool HasRuntimeFiles(JsonElement library) =>
        new[] { "runtime", "runtimeTargets", "native" }.Any(kind =>
            library.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)));

    private static JsonDocument ReadGoModuleData() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("tools", "notices", "kopia-go-modules.json")));

    /// <summary>The Go standard library, then each module: its label in the notices, its folder under kopia-go-licenses, its data.</summary>
    private static IEnumerable<(string Label, string Folder, JsonElement Item)> GoEntries(JsonDocument data)
    {
        var go = data.RootElement.GetProperty("go");
        yield return ($"Go {go.GetProperty("version").GetString()!.Replace("go", "", StringComparison.Ordinal)}", "std", go);
        foreach (var module in data.RootElement.GetProperty("modules").EnumerateArray())
        {
            var path = module.GetProperty("path").GetString()!;
            yield return ($"{path} {module.GetProperty("version").GetString()}", path, module);
        }
    }

    private static string Section(string notices, string heading, string? nextHeading)
    {
        var start = notices.IndexOf($"\n{heading}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"THIRD-PARTY-NOTICES.txt has no '{heading}' section; run {UpdateCommand}.");
        var end = nextHeading is null ? notices.Length : notices.IndexOf($"\n{nextHeading}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"THIRD-PARTY-NOTICES.txt has no '{nextHeading}' section after '{heading}'; run {UpdateCommand}.");
        return notices[start..end];
    }

    /// <summary>
    /// The Go version and the modules ("path version sum") from the build info Go 1.18+ embeds in a binary, the data
    /// `go version -m` prints: a 32-byte header starting "\xff Go buildinf:", then (flag 2) the Go version and the
    /// module info as uvarint-length-prefixed strings, the module info wrapped in two 16-byte sentinels.
    /// </summary>
    private static (string GoVersion, HashSet<string> Modules) ReadGoBuildInfo(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        var at = bytes.AsSpan().IndexOf(Encoding.Latin1.GetBytes("ÿ Go buildinf:"));
        Assert.True(at >= 0 && (bytes[at + 15] & 2) != 0, $"{exePath} has no Go 1.18+ build info.");
        var pos = at + 32;
        var goVersion = Encoding.UTF8.GetString(ReadField(bytes, ref pos)).Split(' ')[0];
        var modInfo = ReadField(bytes, ref pos);
        var modules = Encoding.UTF8.GetString(modInfo[16..^16]).Split('\n')
            .Select(line => line.Split('\t'))
            .Where(fields => fields[0] == "dep")
            .Select(fields => $"{fields[1]} {fields[2]} {(fields.Length > 3 ? fields[3] : "")}")
            .ToHashSet(StringComparer.Ordinal);
        return (goVersion, modules);
    }

    private static byte[] ReadField(byte[] bytes, ref int pos)
    {
        int length = 0, shift = 0;
        byte b;
        do
        {
            b = bytes[pos++];
            length |= (b & 0x7f) << shift;
            shift += 7;
        }
        while (b >= 0x80);

        var field = bytes[pos..(pos + length)];
        pos += length;
        return field;
    }
}
