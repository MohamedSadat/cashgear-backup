using System.Text.Json;
using System.Text.Json.Serialization;

namespace KopiaSpike;

// Shapes from internal/serverapi/serverapi.go at kopia v0.23.1. Responses keep unknown fields in
// Extra so FINDINGS.md can note what we did not expect; requests spell Kopia's JSON names exactly.

public abstract record KopiaResponse
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record RepoStatus : KopiaResponse
{
    public bool Connected { get; init; }
    public string? ConfigFile { get; init; }
    public int? FormatVersion { get; init; }
    public string? Hash { get; init; }
    public string? Encryption { get; init; }
    public string? Ecc { get; init; }
    public string? Splitter { get; init; }
    public long? MaxPackSize { get; init; }
    public string? Storage { get; init; }
    public bool SupportsContentCompression { get; init; }
    public string? Hostname { get; init; }
    public string? Username { get; init; }

    [JsonPropertyName("initTaskID")]
    public string? InitTaskId { get; init; }
}

public sealed record SourceInfo(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("path")] string Path)
{
    public string ToQuery() =>
        $"userName={Uri.EscapeDataString(UserName)}&host={Uri.EscapeDataString(Host)}&path={Uri.EscapeDataString(Path)}";

    public override string ToString() => $"{UserName}@{Host}:{Path}";
}

public sealed record SourceStatus : KopiaResponse
{
    public required SourceInfo Source { get; init; }
    public required string Status { get; init; }
    public JsonElement? LastSnapshot { get; init; }
    public JsonElement? Upload { get; init; }
    public string? CurrentTask { get; init; }
}

public sealed record SourcesResponse : KopiaResponse
{
    public string? LocalUsername { get; init; }
    public string? LocalHost { get; init; }
    public bool MultiUser { get; init; }
    public List<SourceStatus> Sources { get; init; } = [];
}

public sealed record SnapshotSummary : KopiaResponse
{
    public long Size { get; init; }
    public long Files { get; init; }
    public long Symlinks { get; init; }
    public long Dirs { get; init; }
    public int NumFailed { get; init; }
    public int NumIgnoredErrors { get; init; }
}

public sealed record SnapshotItem : KopiaResponse
{
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

public sealed record SnapshotsResponse : KopiaResponse
{
    public List<SnapshotItem> Snapshots { get; init; } = [];
    public int UnfilteredCount { get; init; }
    public int UniqueCount { get; init; }
}

public sealed record TaskCounter(long Value, string? Units, string? Level);

public sealed record TaskInfo : KopiaResponse
{
    public required string Id { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public required string Kind { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public string? ProgressInfo { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, TaskCounter>? Counters { get; init; }

    public bool IsFinished => Status is "SUCCESS" or "FAILED" or "CANCELED";
}

public sealed record TaskListResponse : KopiaResponse
{
    public List<TaskInfo> Tasks { get; init; } = [];
}

public sealed record CreateRepositoryRequest(
    [property: JsonPropertyName("storage")] StorageConnection Storage,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("options")] Dictionary<string, object> Options,
    [property: JsonPropertyName("clientOptions")] ClientOptions ClientOptions)
{
    public override string ToString() => $"CreateRepositoryRequest {{ Storage = {Storage}, Password = *** }}";
}

public sealed record StorageConnection(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("config")] Dictionary<string, object> Config);

public sealed record ClientOptions(
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("description")] string Description);

public sealed record CreateSourceRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("createSnapshot")] bool CreateSnapshot,
    [property: JsonPropertyName("policy")] JsonElement Policy);

public sealed record RestoreRequest(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("fsOutput")] FilesystemOutput FsOutput,
    [property: JsonPropertyName("options")] RestoreOptions Options);

public sealed record FilesystemOutput(
    [property: JsonPropertyName("targetPath")] string TargetPath,
    [property: JsonPropertyName("overwriteFiles")] bool OverwriteFiles = false,
    [property: JsonPropertyName("overwriteDirectories")] bool OverwriteDirectories = false,
    [property: JsonPropertyName("overwriteSymlinks")] bool OverwriteSymlinks = false,
    [property: JsonPropertyName("ignorePermissionErrors")] bool IgnorePermissionErrors = true,
    [property: JsonPropertyName("writeFilesAtomically")] bool WriteFilesAtomically = true,
    [property: JsonPropertyName("skipOwners")] bool SkipOwners = true,
    [property: JsonPropertyName("skipPermissions")] bool SkipPermissions = false,
    [property: JsonPropertyName("skipTimes")] bool SkipTimes = false);

public sealed record RestoreOptions(
    [property: JsonPropertyName("incremental")] bool Incremental = false,
    [property: JsonPropertyName("ignoreErrors")] bool IgnoreErrors = false,
    [property: JsonPropertyName("restoreDirEntryAtDepth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RestoreDirEntryAtDepth = int.MaxValue);
