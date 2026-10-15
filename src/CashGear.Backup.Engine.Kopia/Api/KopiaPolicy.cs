using System.Text.Json.Serialization;

namespace CashGear.Backup.Engine.Kopia.Api;

// The parts of Kopia's per-source policy (snapshot/policy) we own. PUT /api/v1/policy replaces the whole
// policy at that level, unset fields inherit Kopia's global defaults (which do not compress and do prune),
// and Kopia silently drops fields it does not know. So the engine always writes every section below and
// reads it back.

internal sealed record KopiaPolicy
{
    [JsonPropertyName("retention")]
    public required RetentionPolicyDto Retention { get; init; }

    [JsonPropertyName("files")]
    public required FilesPolicyDto Files { get; init; }

    [JsonPropertyName("errorHandling")]
    public required ErrorHandlingPolicyDto ErrorHandling { get; init; }

    [JsonPropertyName("scheduling")]
    public required SchedulingPolicyDto Scheduling { get; init; }

    [JsonPropertyName("compression")]
    public required CompressionPolicyDto Compression { get; init; }

    [JsonPropertyName("osSnapshots")]
    public required OsSnapshotPolicyDto OsSnapshots { get; init; }
}

internal sealed record RetentionPolicyDto(
    [property: JsonPropertyName("keepLatest")] int KeepLatest,
    [property: JsonPropertyName("keepHourly")] int KeepHourly,
    [property: JsonPropertyName("keepDaily")] int KeepDaily,
    [property: JsonPropertyName("keepWeekly")] int KeepWeekly,
    [property: JsonPropertyName("keepMonthly")] int KeepMonthly,
    [property: JsonPropertyName("keepAnnual")] int KeepAnnual,
    [property: JsonPropertyName("ignoreIdenticalSnapshots")] bool IgnoreIdenticalSnapshots);

internal sealed record FilesPolicyDto(
    [property: JsonPropertyName("ignore")] IReadOnlyList<string> Ignore,
    [property: JsonPropertyName("ignoreDotFiles")] IReadOnlyList<string> IgnoreDotFiles);

internal sealed record ErrorHandlingPolicyDto(
    [property: JsonPropertyName("ignoreFileErrors")] bool IgnoreFileErrors,
    [property: JsonPropertyName("ignoreDirectoryErrors")] bool IgnoreDirectoryErrors,
    [property: JsonPropertyName("ignoreUnknownTypes")] bool IgnoreUnknownTypes);

internal sealed record SchedulingPolicyDto(
    [property: JsonPropertyName("manual")] bool Manual);

internal sealed record CompressionPolicyDto(
    [property: JsonPropertyName("compressorName")] string CompressorName,
    [property: JsonPropertyName("neverCompress")] IReadOnlyList<string> NeverCompress);

internal sealed record OsSnapshotPolicyDto(
    [property: JsonPropertyName("volumeShadowCopy")] VolumeShadowCopyPolicyDto VolumeShadowCopy);

/// <summary>Kopia's OSSnapshotMode is a number on the wire: 0 never, 1 always, 2 when-available.</summary>
internal sealed record VolumeShadowCopyPolicyDto(
    [property: JsonPropertyName("enable")] int Enable)
{
    public const int Never = 0;
    public const int Always = 1;
    public const int WhenAvailable = 2;
}
