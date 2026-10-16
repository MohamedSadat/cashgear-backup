using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CashGear.Backup.Core;

namespace CashGear.Backup.Engine.Kopia;

public sealed partial class KopiaBackupEngine
{
    /// <summary>
    /// <c>kopia snapshot verify</c> on the job's source (the server has no REST call for it): every snapshot's
    /// structure, plus <paramref name="sampleFraction"/> of the files read back. It runs on a copy of the
    /// repository config whose content cache is a new, empty folder: reads served from the existing cache are not
    /// checked against the destination, so damage there would go unseen (FINDINGS, Phase 3).
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(BackupJob job, double sampleFraction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (double.IsNaN(sampleFraction) || sampleFraction is < 0 or > 1)
        {
            throw new BackupEngineException(BackupErrorKind.InvalidRequest, "The share of files to read back must be between 0 and 1.");
        }

        var session = Session(job.RepositoryId);
        var layout = session.Host.Layout;
        var scratch = Path.Combine(layout.Root, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var config = Path.Combine(scratch, "repository.config");
            await WriteConfigWithEmptyCacheAsync(layout.ConfigFile, config, Path.Combine(scratch, "cache"), ct);
            var result = await session.Host.RunCommandAsync(
            [
                "snapshot", "verify",
                $"--config-file={config}",
                $"--log-dir={layout.Logs}",
                "--no-persist-credentials",
                "--no-use-credential-manager",
                "--no-progress",
                "--json",
                "--verify-files-percent=" + (sampleFraction * 100).ToString("0.###", CultureInfo.InvariantCulture),
                $"--sources={session.Source(NormalizeSourcePath(job.SourcePath))}",
            ], ct);
            var verify = ParseVerifyOutput(result.StandardOutput);
            if (verify?.Stats is not { } stats)
            {
                var reason = result.ErrorTail.LastOrDefault(l => l.Trim().Length > 0) ?? $"exit code {result.ExitCode}";
                throw new BackupEngineException(
                    reason.Contains("storage", StringComparison.OrdinalIgnoreCase) ? BackupErrorKind.DestinationUnreachable : BackupErrorKind.EngineProtocolError,
                    "The check did not finish: " + reason);
            }

            var passed = result.ExitCode == 0 && verify.ErrorCount == 0;
            IReadOnlyList<string> errors = verify.ErrorStrings is { Count: > 0 } found ? found
                : passed ? []
                : [$"The check reported {verify.ErrorCount} problem(s) (exit code {result.ExitCode})."];
            return new VerifyResult(passed, stats.ProcessedObjectCount, errors) { FilesRead = stats.ReadFileCount, BytesRead = stats.ReadBytes };
        }
        finally
        {
            await DeleteDirectoryAsync(scratch);
        }
    }

    /// <summary>Not available: Kopia's server runs maintenance itself once it owns a repository (FINDINGS, Phase 1a #8, decided in Phase 3).</summary>
    public Task RunMaintenanceAsync(RepositoryInfo repo, bool full, CancellationToken ct) =>
        throw new NotSupportedException("Kopia's server runs maintenance itself for the repositories it opens; there must be one owner of maintenance.");

    private static async Task WriteConfigWithEmptyCacheAsync(string source, string target, string cacheDirectory, CancellationToken ct)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(source, ct))?.AsObject()
            ?? throw Protocol("The repository config is not a JSON object.");
        if (config["caching"] is not JsonObject caching)
        {
            caching = [];
            config["caching"] = caching;
        }

        caching["cacheDirectory"] = cacheDirectory;
        await File.WriteAllTextAsync(target, config.ToJsonString(), ct);
    }

    /// <summary>The JSON summary is the last line of standard output that is a JSON object.</summary>
    private static VerifyOutput? ParseVerifyOutput(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<VerifyOutput>(line);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Best effort: the CLI may still hold its log or cache files for a moment after it exits.</summary>
    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100 * (attempt + 1), CancellationToken.None);
            }
        }
    }

    private sealed record VerifyOutput(
        [property: JsonPropertyName("stats")] VerifyStats? Stats,
        [property: JsonPropertyName("errorCount")] int ErrorCount,
        [property: JsonPropertyName("errorStrings")] List<string>? ErrorStrings);

    private sealed record VerifyStats(
        [property: JsonPropertyName("processedObjectCount")] long ProcessedObjectCount,
        [property: JsonPropertyName("readFileCount")] long ReadFileCount,
        [property: JsonPropertyName("readBytes")] long ReadBytes);
}
