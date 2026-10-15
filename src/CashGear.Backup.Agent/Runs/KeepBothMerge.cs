using System.Security.Cryptography;
using CashGear.Backup.Core;

namespace CashGear.Backup.Agent.Runs;

/// <summary>What <see cref="KeepBothMerge.Merge"/> did.</summary>
internal sealed record MergeOutcome
{
    /// <summary>Items that were missing and are back.</summary>
    public int Moved { get; set; }

    /// <summary>Items that were there and differ: kept, with the restored copy beside them.</summary>
    public int KeptBoth { get; set; }

    /// <summary>Files that were there with the same content: left as they were.</summary>
    public int Identical { get; set; }

    /// <summary>Items that could not be put in place; they stay in the staging folder.</summary>
    public List<FailedItem> Failed { get; } = [];

    public string Describe(string into, string stamp)
    {
        var parts = new List<string> { $"Restored into {into}." };
        if (KeptBoth > 0)
        {
            parts.Add($"{Plural(KeptBoth, "file was", "files were")} already there and different: they were kept, and the restored copies are next to them with \"(restored {stamp})\" in the name.");
        }

        if (Identical > 0)
        {
            parts.Add($"{Plural(Identical, "identical file was", "identical files were")} left as they were.");
        }

        if (Failed.Count > 0)
        {
            parts.Add($"{Plural(Failed.Count, "item", "items")} could not be put in place.");
        }

        return string.Join(" ", parts);
    }

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}

/// <summary>
/// Moves a freshly restored tree (staged in a new, empty folder, as every engine restore is) into the job's own
/// folder without replacing anything: missing items move in, identical files are dropped from the staging folder,
/// and anything that differs stays as it is with the restored copy beside it as "name (restored stamp).ext".
/// Links are moved as links and never followed, so the merge cannot reach outside the staging folder.
/// </summary>
internal static class KeepBothMerge
{
    public static MergeOutcome Merge(string from, string into, string stamp)
    {
        var outcome = new MergeOutcome();
        if (File.Exists(into) || IsLink(into))
        {
            // A file or link now stands where the restored folder belongs: keep it, restore the folder beside it.
            into = Alternative(into, stamp);
        }

        Directory.CreateDirectory(into);
        MergeDirectory(from, into, "", stamp, outcome);
        return outcome;
    }

    private static void MergeDirectory(string from, string into, string relative, string stamp, MergeOutcome outcome)
    {
        foreach (var source in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(source);
            var itemRelative = relative.Length == 0 ? name : relative + "/" + name;
            var target = Path.Combine(into, name);
            try
            {
                var info = new FileInfo(source);
                var isLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (info.Attributes.HasFlag(FileAttributes.Directory) && !isLink)
                {
                    // Merge into a real folder only: a file or a link standing there is kept, the folder goes beside it.
                    if (File.Exists(target) || IsLink(target))
                    {
                        target = Alternative(target, stamp);
                    }

                    Directory.CreateDirectory(target);
                    MergeDirectory(source, target, itemRelative, stamp, outcome);
                    continue;
                }

                if (!File.Exists(target) && !Directory.Exists(target))
                {
                    MoveEntry(source, target, info);
                    outcome.Moved++;
                }
                else if (!isLink && File.Exists(target) && SameContent(source, target))
                {
                    File.Delete(source);
                    outcome.Identical++;
                }
                else
                {
                    MoveEntry(source, Alternative(target, stamp), info);
                    outcome.KeptBoth++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome.Failed.Add(new FailedItem(itemRelative, ex.Message));
            }
        }

        if (IsEmpty(from))
        {
            Directory.Delete(from);
        }
    }

    /// <summary>Never overwrites: both moves fail if the target appeared meanwhile.</summary>
    private static void MoveEntry(string source, string target, FileInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target, overwrite: false);
        }
    }

    /// <summary>"report.docx" becomes "report (restored 2026-10-15 1432).docx", then "... 1432) 2.docx" if that exists too.</summary>
    private static string Alternative(string path, string stamp)
    {
        var folder = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} (restored {stamp}){(n == 1 ? "" : $" {n}")}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
        {
            return false;
        }

        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool IsEmpty(string directory) => Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any();
}
