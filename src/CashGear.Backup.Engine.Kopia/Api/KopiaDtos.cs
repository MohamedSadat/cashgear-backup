using System.Text.Json;
using System.Text.Json.Serialization;

namespace CashGear.Backup.Engine.Kopia.Api;

// Shapes of kopia v0.23.1's REST API (internal/serverapi), checked by the contract tests. Responses keep
// unknown fields in Extra so a Kopia bump that adds fields is visible; requests spell Kopia's JSON names.

internal abstract record KopiaResponse
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

internal sealed record RepoStatus : KopiaResponse
{
    public bool Connected { get; init; }
    public string? ConfigFile { get; init; }
    public int? FormatVersion { get; init; }
    public string? Hash { get; init; }
    public string? Encryption { get; init; }
    public string? Splitter { get; init; }
    public long? MaxPackSize { get; init; }
    public string? Storage { get; init; }
    public bool SupportsContentCompression { get; init; }
    public string? Hostname { get; init; }
    public string? Username { get; init; }
    public string? Description { get; init; }

    [JsonPropertyName("initTaskID")]
    public string? InitTaskId { get; init; }
}

internal sealed record AlgorithmOption(string Id, bool Deprecated);

internal sealed record AlgorithmsResponse : KopiaResponse
{
    public List<AlgorithmOption> Compression { get; init; } = [];
    public List<AlgorithmOption> Encryption { get; init; } = [];
    public List<AlgorithmOption> Hash { get; init; } = [];
}

internal sealed record SourceInfo(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("path")] string Path)
{
    public string ToQuery() =>
        $"userName={Uri.EscapeDataString(UserName)}&host={Uri.EscapeDataString(Host)}&path={Uri.EscapeDataString(Path)}";

    /// <summary>How Kopia names the source in task descriptions and upload responses.</summary>
    public override string ToString() => $"{UserName}@{Host}:{Path}";
}

internal sealed record UploadCounters : KopiaResponse
{
    public long HashedBytes { get; init; }
    public long HashedFiles { get; init; }
    public long CachedBytes { get; init; }
    public long CachedFiles { get; init; }
    public long EstimatedBytes { get; init; }
    public long EstimatedFiles { get; init; }
    public long Errors { get; init; }
    public long IgnoredErrors { get; init; }
}

internal sealed record SourceStatus : KopiaResponse
{
    public required SourceInfo Source { get; init; }
    public required string Status { get; init; }
    public UploadCounters? Upload { get; init; }
    public string? CurrentTask { get; init; }
}

internal sealed record SourcesResponse : KopiaResponse
{
    public string? LocalUsername { get; init; }
    public string? LocalHost { get; init; }
    public bool MultiUser { get; init; }
    public List<SourceStatus> Sources { get; init; } = [];
}

internal sealed record UploadResult(bool Success);

internal sealed record UploadResponse : KopiaResponse
{
    public Dictionary<string, UploadResult> Sources { get; init; } = [];
}

internal sealed record SnapshotError(string? Path, string? Error);

internal sealed record SnapshotSummary : KopiaResponse
{
    public long Size { get; init; }
    public long Files { get; init; }
    public long Symlinks { get; init; }
    public long Dirs { get; init; }
    public int NumFailed { get; init; }
    public int NumIgnoredErrors { get; init; }
    public List<SnapshotError>? Errors { get; init; }
}

internal sealed record SnapshotItem : KopiaResponse
{
    /// <summary>Set by Kopia on partial snapshots: "canceled", or "checkpoint" while a long upload runs.</summary>
    public const string IncompleteCanceled = "canceled";
    public const string IncompleteCheckpoint = "checkpoint";

    public required string Id { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }

    [JsonPropertyName("incomplete")]
    public string? IncompleteReason { get; init; }

    public SnapshotSummary? Summary { get; init; }

    [JsonPropertyName("rootID")]
    public required string RootId { get; init; }

    public List<string>? Retention { get; init; }
}

internal sealed record SnapshotsResponse : KopiaResponse
{
    public List<SnapshotItem> Snapshots { get; init; } = [];
    public int UnfilteredCount { get; init; }
    public int UniqueCount { get; init; }
}

internal sealed record TaskCounter(long Value, string? Units, string? Level);

internal sealed record TaskInfo : KopiaResponse
{
    public const string KindSnapshot = "Snapshot";
    public const string KindRestore = "Restore";
    public const string StatusSuccess = "SUCCESS";
    public const string StatusFailed = "FAILED";
    public const string StatusCanceled = "CANCELED";

    public required string Id { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public required string Kind { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public string? ProgressInfo { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, TaskCounter>? Counters { get; init; }

    public bool IsFinished => Status is StatusSuccess or StatusFailed or StatusCanceled;

    public long Counter(string name) => Counters is not null && Counters.TryGetValue(name, out var c) ? c.Value : 0;
}

internal sealed record TaskListResponse : KopiaResponse
{
    public List<TaskInfo> Tasks { get; init; } = [];
}

/// <summary>A directory object from <c>GET /api/v1/objects/{id}</c>.</summary>
internal sealed record DirectoryManifest : KopiaResponse
{
    private readonly List<DirectoryEntry> _entries = [];

    public string? Stream { get; init; }

    /// <summary>Kopia writes <c>"entries": null</c> for an empty folder.</summary>
    public List<DirectoryEntry> Entries { get => _entries; init => _entries = value ?? []; }
    public SnapshotSummary? Summary { get; init; }
}

internal sealed record DirectoryEntry : KopiaResponse
{
    public const string TypeFile = "f";
    public const string TypeDirectory = "d";
    public const string TypeSymlink = "s";

    public required string Name { get; init; }
    public string? Type { get; init; }
    public string? Mode { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Mtime { get; init; }

    [JsonPropertyName("obj")]
    public string? ObjectId { get; init; }

    [JsonPropertyName("summ")]
    public SnapshotSummary? Summary { get; init; }
}

internal sealed record StorageConnection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("config")] Dictionary<string, object> Config);

internal sealed record ClientOptions(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("description")] string Description);

/// <summary>Body of <c>repo/create</c> and <c>repo/connect</c>. Holds the repository password: never log it.</summary>
internal sealed record RepositoryRequest(
    [property: JsonPropertyName("storage")] StorageConnection Storage,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("clientOptions")] ClientOptions ClientOptions)
{
    /// <summary>Only sent by create: format and encryption options; empty means Kopia's defaults.</summary>
    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Options { get; init; }

    public override string ToString() => $"RepositoryRequest {{ Storage = {Storage.Type}, Password = *** }}";
}

internal sealed record CreateSourceRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("createSnapshot")] bool CreateSnapshot,
    [property: JsonPropertyName("policy")] KopiaPolicy Policy);

internal sealed record ResolvePolicyRequest(
    [property: JsonPropertyName("updates")] JsonElement Updates,
    [property: JsonPropertyName("numUpcomingSnapshotTimes")] int NumUpcomingSnapshotTimes = 0);

internal sealed record ResolvePolicyResponse : KopiaResponse
{
    public JsonElement Effective { get; init; }
    public JsonElement Definition { get; init; }
}

internal sealed record ThrottleLimits
{
    [JsonPropertyName("maxUploadSpeedBytesPerSecond")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxUploadSpeedBytesPerSecond { get; init; }
}

internal sealed record RestoreRequestDto(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("fsOutput")] FilesystemOutput FsOutput,
    [property: JsonPropertyName("options")] RestoreOptions Options);

/// <summary>Restore output. The overwrite flags are false and must stay false (CLAUDE.md rule 4).</summary>
internal sealed record FilesystemOutput(
    [property: JsonPropertyName("targetPath")] string TargetPath,
    [property: JsonPropertyName("overwriteFiles")] bool OverwriteFiles = false,
    [property: JsonPropertyName("overwriteDirectories")] bool OverwriteDirectories = false,
    [property: JsonPropertyName("overwriteSymlinks")] bool OverwriteSymlinks = false,
    [property: JsonPropertyName("ignorePermissionErrors")] bool IgnorePermissionErrors = true,
    [property: JsonPropertyName("writeFilesAtomically")] bool WriteFilesAtomically = true,
    [property: JsonPropertyName("skipOwners")] bool SkipOwners = true,
    [property: JsonPropertyName("skipPermissions")] bool SkipPermissions = false,
    [property: JsonPropertyName("skipTimes")] bool SkipTimes = false);

/// <summary>
/// Restore options. <c>restoreDirEntryAtDepth</c> must be int.MaxValue: Kopia's REST default (0) writes
/// <c>.kopia-entry</c> placeholder files instead of the data and still reports SUCCESS.
/// </summary>
internal sealed record RestoreOptions(
    [property: JsonPropertyName("incremental")] bool Incremental = false,
    [property: JsonPropertyName("ignoreErrors")] bool IgnoreErrors = false,
    [property: JsonPropertyName("restoreDirEntryAtDepth")] int RestoreDirEntryAtDepth = int.MaxValue);
