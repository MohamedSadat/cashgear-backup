using System.Runtime.CompilerServices;
using CashGear.Backup.Core;

namespace CashGear.Backup.Agent.Runs;

/// <summary>One batch of a search: the matches found in the folders searched since the last batch.</summary>
internal sealed record SearchBatch(IReadOnlyList<SnapshotEntry> Matches, bool Done, bool Truncated, int FoldersSearched);

/// <summary>
/// Name search inside a snapshot. The engine has no search, so this walks the snapshot folder by folder (breadth
/// first, so shallow matches come first) and stops at the result limit or after <see cref="MaxFolders"/> folders.
/// </summary>
internal static class SnapshotSearch
{
    public const int MaxFolders = 20_000;

    public static async IAsyncEnumerable<SearchBatch> SearchAsync(IBackupEngine engine, SnapshotInfo snapshot, string query, int maxResults,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<string>([""]);
        var folders = 0;
        var found = 0;
        var skipped = false;
        while (pending.Count > 0 && found < maxResults && folders < MaxFolders)
        {
            var folder = pending.Dequeue();
            var entries = await engine.BrowseAsync(snapshot, folder, ct);
            folders++;
            var matches = new List<SnapshotEntry>();
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Kind == SnapshotEntryKind.Directory)
                {
                    pending.Enqueue(entry.Path);
                }

                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (found < maxResults)
                    {
                        matches.Add(entry);
                        found++;
                    }
                    else
                    {
                        skipped = true;
                    }
                }
            }

            if (matches.Count > 0)
            {
                yield return new SearchBatch(matches, Done: false, Truncated: false, folders);
            }
        }

        yield return new SearchBatch([], Done: true, Truncated: skipped || (pending.Count > 0 && (found >= maxResults || folders >= MaxFolders)), folders);
    }
}
