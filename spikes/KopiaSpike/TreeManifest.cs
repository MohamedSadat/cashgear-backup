using System.Security.Cryptography;

namespace KopiaSpike;

public sealed record TreeEntry(string Path, bool IsDirectory, long Size, string? Sha256, DateTime LastWriteUtc, FileAttributes Attributes);

/// <summary>
/// Snapshot of a directory tree for byte-compare: every file's SHA-256, plus the exact set of files
/// and directories (empty ones included). Times and attributes are captured for reporting only.
/// </summary>
public sealed class TreeManifest
{
    private TreeManifest(SortedDictionary<string, TreeEntry> entries) => Entries = entries;

    public IReadOnlyDictionary<string, TreeEntry> Entries { get; }
    public int FileCount => Entries.Values.Count(e => !e.IsDirectory);
    public int DirectoryCount => Entries.Values.Count(e => e.IsDirectory);
    public long TotalBytes => Entries.Values.Sum(e => e.Size);

    public static TreeManifest Capture(string root)
    {
        var entries = new SortedDictionary<string, TreeEntry>(StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0, // the default skips Hidden and System entries
            IgnoreInaccessible = false,
        };

        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            entries[relative] = info is FileInfo file
                ? new TreeEntry(relative, false, file.Length, Hash(file.FullName), file.LastWriteTimeUtc, file.Attributes)
                : new TreeEntry(relative, true, 0, null, info.LastWriteTimeUtc, info.Attributes);
        }

        return new TreeManifest(entries);
    }

    /// <summary>Content differences that fail a byte-compare: missing, extra, kind, size or hash.</summary>
    public List<string> ContentDifferences(TreeManifest actual)
    {
        var differences = new List<string>();
        foreach (var (path, expected) in Entries)
        {
            if (!actual.Entries.TryGetValue(path, out var got))
            {
                differences.Add($"missing: {path}");
            }
            else if (expected.IsDirectory != got.IsDirectory)
            {
                differences.Add($"kind differs: {path}");
            }
            else if (expected.Size != got.Size || expected.Sha256 != got.Sha256)
            {
                differences.Add($"content differs: {path} ({expected.Size} vs {got.Size} bytes)");
            }
        }

        differences.AddRange(actual.Entries.Keys.Where(k => !Entries.ContainsKey(k)).Select(k => $"extra: {k}"));
        return differences;
    }

    /// <summary>How well metadata survived (reported, not asserted).</summary>
    public object MetadataReport(TreeManifest actual)
    {
        var common = Entries.Values.Where(e => actual.Entries.ContainsKey(e.Path)).ToList();
        var files = common.Where(e => !e.IsDirectory).ToList();
        var dirs = common.Where(e => e.IsDirectory).ToList();
        return new
        {
            filesCompared = files.Count,
            fileMtimesPreserved = files.Count(e => e.LastWriteUtc == actual.Entries[e.Path].LastWriteUtc),
            dirsCompared = dirs.Count,
            dirMtimesPreserved = dirs.Count(e => e.LastWriteUtc == actual.Entries[e.Path].LastWriteUtc),
            dirMtimesDiffering = dirs.Where(e => e.LastWriteUtc != actual.Entries[e.Path].LastWriteUtc).Select(e => e.Path).ToList(),
            readOnlyPreserved = AttributeKept(actual, FileAttributes.ReadOnly),
            hiddenPreserved = AttributeKept(actual, FileAttributes.Hidden),
        };
    }

    private string AttributeKept(TreeManifest actual, FileAttributes attribute)
    {
        var withIt = Entries.Values.Where(e => e.Attributes.HasFlag(attribute)).ToList();
        var kept = withIt.Count(e => actual.Entries.TryGetValue(e.Path, out var got) && got.Attributes.HasFlag(attribute));
        return $"{kept}/{withIt.Count}";
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
