using System.Text.Json;
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

    /// <summary>Already-compressed formats; compressing them again only costs CPU.</summary>
    public static readonly IReadOnlyList<string> NeverCompress =
    [
        ".7z", ".zip", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac",
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
    ];

    public static string CompressorName(BackupCompression compression) => throw new NotImplementedException();

    public static KopiaPolicy ToPolicy(BackupJob job) => throw new NotImplementedException();

    /// <summary>
    /// Where the policy Kopia has stored for the source (<c>GET /policy</c>) differs from <paramref name="expected"/>.
    /// Empty lists and objects count as unset on both sides, as Kopia omits them.
    /// </summary>
    public static IReadOnlyList<string> Differences(KopiaPolicy expected, JsonElement? stored) => throw new NotImplementedException();
}
