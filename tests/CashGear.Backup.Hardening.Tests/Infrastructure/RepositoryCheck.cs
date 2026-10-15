using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>What <see cref="RepositoryCheck"/> found.</summary>
internal sealed record RepositoryCheckResult(
    int Snapshots, int IncompleteSnapshots, int SnapshotErrors, long FilesRead, int ContentErrors, bool CommandsSucceeded, string Report)
{
    /// <summary>Every snapshot's structure and every file read back without error, and every content backed by a valid blob.</summary>
    public bool Clean => CommandsSucceeded && SnapshotErrors == 0 && ContentErrors == 0;
}

/// <summary>
/// A consistency check of a destination's whole repository with the pinned kopia CLI, independent of the agent:
/// every snapshot of every source verified with all files read back (<c>snapshot verify</c>), then every content
/// checked against its pack, downloading all packs (<c>content verify --full</c>). Runs on a copy of the agent's
/// repository.config with an empty cache, so nothing is served from a cache the agent filled (FINDINGS, Phase 3 #2).
/// The password goes only into the child's environment.
/// </summary>
internal static class RepositoryCheck
{
    public static async Task<RepositoryCheckResult> RunAsync(AgentProcess agent, string destinationId, CancellationToken ct,
        IReadOnlyDictionary<string, string>? storageEnvironment = null)
    {
        var source = Path.Combine(agent.Workspace.Data, "engine", "repos", destinationId, "repository.config");
        var scratch = Path.Combine(agent.Workspace.Root, "check-" + RandomNumberGenerator.GetHexString(6, lowercase: true));
        Directory.CreateDirectory(scratch);
        // Only the cache folder changes: kopia panics when the cache sizes are missing (zero).
        var config = JsonNode.Parse(await File.ReadAllTextAsync(source, ct))!.AsObject();
        config["caching"]!["cacheDirectory"] = Path.Combine(scratch, "cache");
        var configFile = Path.Combine(scratch, "repository.config");
        await File.WriteAllTextAsync(configFile, config.ToJsonString(), ct);

        var report = new StringBuilder();
        var ok = true;

        var list = await KopiaAsync(["snapshot", "list", "--all", "--json"]);
        var snapshots = list.ExitCode == 0 ? JsonNode.Parse(list.Output)!.AsArray() : [];
        var incomplete = snapshots.Count(s => s?["incomplete"] is not null);
        report.AppendLine($"snapshot list: exit {list.ExitCode}, {snapshots.Count} snapshot(s), {incomplete} incomplete");
        if (list.ExitCode != 0)
        {
            report.AppendLine("  " + string.Join(Environment.NewLine + "  ", list.Errors.Take(10)));
        }

        var verify = await KopiaAsync(["snapshot", "verify", "--verify-files-percent=100", "--json"]);
        var verifyJson = LastJsonObject(verify.Output);
        var snapshotErrors = verifyJson?["errorCount"]?.GetValue<int>() ?? -1;
        var filesRead = verifyJson?["stats"]?["readFileCount"]?.GetValue<long>() ?? 0;
        report.AppendLine($"snapshot verify (all snapshots, 100 % of files read): exit {verify.ExitCode}, {snapshotErrors} error(s), {filesRead} file(s) read");
        if (verifyJson?["errorStrings"] is JsonArray { Count: > 0 } errorStrings)
        {
            report.AppendLine("  " + string.Join(Environment.NewLine + "  ", errorStrings.Take(10)));
        }
        else if (verify.ExitCode != 0)
        {
            report.AppendLine("  " + string.Join(Environment.NewLine + "  ", verify.Errors.Take(10)));
        }

        var content = await KopiaAsync(["content", "verify", "--full"]);
        var contentErrors = content.ExitCode == 0 ? 0 : Math.Max(1, CountFromSummary(content.Errors));
        report.AppendLine($"content verify --full: exit {content.ExitCode}; {content.Errors.LastOrDefault(l => l.Contains("Finished", StringComparison.Ordinal)) ?? "(no summary)"}");
        if (content.ExitCode != 0)
        {
            report.AppendLine("  " + string.Join(Environment.NewLine + "  ", content.Errors.Take(10)));
        }

        ok &= list.ExitCode == 0 && verify.ExitCode == 0 && verifyJson is not null && content.ExitCode == 0;
        if (!HardeningSettings.KeepWorkspace)
        {
            Workspace.ForceDelete(scratch);
        }

        return new RepositoryCheckResult(snapshots.Count, incomplete, Math.Max(snapshotErrors, 0) + (verifyJson is null ? 1 : 0), filesRead, contentErrors, ok, report.ToString());

        async Task<(int ExitCode, string Output, IReadOnlyList<string> Errors)> KopiaAsync(string[] command)
        {
            var start = new ProcessStartInfo(KopiaTestBinary.ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = scratch,
            };
            foreach (var argument in command.Concat([$"--config-file={configFile}", $"--log-dir={Path.Combine(scratch, "logs")}",
                "--no-persist-credentials", "--no-use-credential-manager", "--no-progress"]))
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                start.Environment.Remove(key);
            }

            start.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
            start.Environment["KOPIA_PASSWORD"] = agent.Password;
            foreach (var (key, value) in storageEnvironment ?? new Dictionary<string, string>())
            {
                start.Environment[key] = value;
            }

            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await output, (await errors).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }

    private static JsonNode? LastJsonObject(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (line.StartsWith('{'))
            {
                try
                {
                    return JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>"Finished verifying 12 contents, found 3 errors." → 3.</summary>
    private static int CountFromSummary(IReadOnlyList<string> lines)
    {
        var summary = lines.LastOrDefault(l => l.Contains("found", StringComparison.Ordinal) && l.Contains("error", StringComparison.Ordinal));
        var words = summary?.Split(' ') ?? [];
        var index = Array.IndexOf(words, "found");
        return index >= 0 && index + 1 < words.Length && int.TryParse(words[index + 1], out var count) ? count : 0;
    }
}
