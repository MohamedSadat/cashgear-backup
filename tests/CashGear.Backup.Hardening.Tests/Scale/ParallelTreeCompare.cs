using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CashGear.Backup.Hardening.Tests.Scale;

/// <summary>
/// The byte-compare of the shared <c>TreeManifest</c> (same set of files and folders, same size and SHA-256 per file),
/// hashing files on every core. A million freshly restored files take hours one at a time, mostly in the virus
/// scanner, which scans each new file as it is first opened; it keeps up better with several opens at once.
/// </summary>
internal static class ParallelTreeCompare
{
    public static List<string> Differences(string expectedRoot, string actualRoot)
    {
        var expected = Capture(expectedRoot);
        var actual = Capture(actualRoot);
        var differences = new List<string>();
        foreach (var (path, entry) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(path, out var got))
            {
                differences.Add($"missing: {path}");
            }
            else if (entry.IsDirectory != got.IsDirectory)
            {
                differences.Add($"kind differs: {path}");
            }
            else if (entry.Size != got.Size || entry.Sha256 != got.Sha256)
            {
                differences.Add($"content differs: {path} ({entry.Size} vs {got.Size} bytes)");
            }
        }

        differences.AddRange(actual.Keys.Where(k => !expected.ContainsKey(k)).Order(StringComparer.Ordinal).Select(k => $"extra: {k}"));
        return differences;
    }

    private static ConcurrentDictionary<string, (bool IsDirectory, long Size, string? Sha256)> Capture(string root)
    {
        var entries = new ConcurrentDictionary<string, (bool, long, string?)>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        Parallel.ForEach(new DirectoryInfo(root).EnumerateFileSystemInfos("*", options), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, info =>
        {
            var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            if (info is FileInfo file)
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
                entries[relative] = (false, file.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }
            else
            {
                entries[relative] = (true, 0, null);
            }
        });
        return entries;
    }
}
