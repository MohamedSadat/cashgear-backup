using System.Text.Json;
using System.Text.Json.Nodes;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;

namespace CashGear.Backup.Engine.Kopia.Policy;

/// <summary>Maps a <see cref="BackupJob"/> to the whole Kopia policy we write for its source (CLAUDE.md rule 8).</summary>
internal static class KopiaPolicyMapper
{
    /// <summary>
    /// Name of the per-folder ignore file Kopia honours inside a source. Kopia's global default is
    /// <c>.kopiaignore</c>, and an empty list counts as "inherit", so we name our own file to make sure a
    /// stray <c>.kopiaignore</c> (for example from a user's own Kopia) never silently excludes data.
    /// </summary>
    public const string IgnoreFileName = ".cashgearignore";

    /// <summary>Kopia's name for "store as is"; not listed by <c>/repo/algorithms</c>.</summary>
    public const string NoCompression = "none";

    /// <summary>Already-compressed formats; compressing them again only costs CPU.</summary>
    public static readonly IReadOnlyList<string> NeverCompress =
    [
        ".7z", ".zip", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac",
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
    ];

    public static string CompressorName(BackupCompression compression) => compression switch
    {
        BackupCompression.Off => NoCompression,
        BackupCompression.Fastest => "zstd-fastest",
        BackupCompression.Balanced => "zstd",
        BackupCompression.Smallest => "zstd-better-compression",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "Unknown compression."),
    };

    public static KopiaPolicy ToPolicy(BackupJob job)
    {
        var r = job.Retention;
        return new KopiaPolicy
        {
            // ignoreIdenticalSnapshots=false: an unchanged source still records a snapshot, so every
            // successful run has exactly one new snapshot to point at.
            Retention = new RetentionPolicyDto(r.KeepLatest, r.KeepHourly, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepAnnual, IgnoreIdenticalSnapshots: false),
            Files = new FilesPolicyDto([.. job.Excludes], [IgnoreFileName]),

            // Unreadable files count as failures (numFailed), never as silently ignored errors; the run
            // still completes and is reported as CompletedWithWarnings with the list.
            ErrorHandling = new ErrorHandlingPolicyDto(IgnoreFileErrors: false, IgnoreDirectoryErrors: false, IgnoreUnknownTypes: true),

            // Our scheduler triggers every run; Kopia must never start one by itself.
            Scheduling = new SchedulingPolicyDto(Manual: true),
            Compression = new CompressionPolicyDto(CompressorName(job.Compression), job.Compression == BackupCompression.Off ? [] : NeverCompress),

            // Open/locked files through VSS when the agent may use it (LocalSystem); plain reads otherwise.
            OsSnapshots = new OsSnapshotPolicyDto(new VolumeShadowCopyPolicyDto(VolumeShadowCopyPolicyDto.WhenAvailable)),
        };
    }

    /// <summary>
    /// Where the policy Kopia has stored for the source (<c>GET /policy</c>) differs from <paramref name="expected"/>.
    /// Empty lists and objects count as unset on both sides, as Kopia omits them.
    /// </summary>
    public static IReadOnlyList<string> Differences(KopiaPolicy expected, JsonElement? stored)
    {
        if (stored is null)
        {
            return ["no policy is defined for the source"];
        }

        var want = Normalize(JsonSerializer.SerializeToNode(expected, KopiaApiClient.Json));
        var have = Normalize(JsonNode.Parse(stored.Value.GetRawText()));
        var differences = new List<string>();
        Compare(want, have, "", differences);
        return differences;
    }

    private static void Compare(JsonNode? want, JsonNode? have, string path, List<string> differences)
    {
        // Recurse while either side is an object (the other unset or an object), so drift is reported per field.
        if (want is JsonObject or null && have is JsonObject or null && (want ?? have) is not null)
        {
            var wantObject = want as JsonObject ?? [];
            var haveObject = have as JsonObject ?? [];
            foreach (var name in wantObject.Select(p => p.Key).Union(haveObject.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                Compare(wantObject[name], haveObject[name], path.Length == 0 ? name : $"{path}.{name}", differences);
            }

            return;
        }

        var wantText = want?.ToJsonString() ?? "(unset)";
        var haveText = have?.ToJsonString() ?? "(unset)";
        if (wantText != haveText)
        {
            differences.Add($"{path}: expected {wantText}, Kopia has {haveText}");
        }
    }

    /// <summary>Drops nulls, empty arrays and empty objects, recursively.</summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    if (Normalize(value) is { } normalized)
                    {
                        result[name] = normalized;
                    }
                }

                return result.Count == 0 ? null : result;
            case JsonArray array:
                return array.Count == 0 ? null : JsonNode.Parse(array.ToJsonString());
            case null:
                return null;
            default:
                return JsonNode.Parse(node.ToJsonString());
        }
    }
}
