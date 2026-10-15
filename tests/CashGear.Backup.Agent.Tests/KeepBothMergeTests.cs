using CashGear.Backup.Agent.Runs;

namespace CashGear.Backup.Agent.Tests;

/// <summary>The merge behind "restore to the original location": it never replaces anything.</summary>
public sealed class KeepBothMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-merge-tests", Guid.NewGuid().ToString("N"));

    private string Staged => Path.Combine(_root, "staged");
    private string Target => Path.Combine(_root, "target");

    [Fact]
    public void Moves_new_files_keeps_both_when_different_and_drops_identical_staged_copies()
    {
        Write(Staged, "new.txt", "new");
        Write(Staged, "same.txt", "same");
        Write(Staged, "diff.txt", "from backup");
        Write(Staged, @"sub\deep.txt", "deep");
        Write(Target, "same.txt", "same");
        Write(Target, "diff.txt", "edited");

        var outcome = KeepBothMerge.Merge(Staged, Target, "2026-10-15 1432");

        Assert.Equal((2, 1, 1, 0), (outcome.Moved, outcome.KeptBoth, outcome.Identical, outcome.Failed.Count));
        Assert.Equal("new", Read(Target, "new.txt"));
        Assert.Equal("same", Read(Target, "same.txt"));
        Assert.Equal("edited", Read(Target, "diff.txt"));
        Assert.Equal("from backup", Read(Target, "diff (restored 2026-10-15 1432).txt"));
        Assert.Equal("deep", Read(Target, @"sub\deep.txt"));
        // Everything moved or was identical: the staging folder is gone.
        Assert.False(Directory.Exists(Staged));
    }

    [Fact]
    public void A_second_restore_with_the_same_stamp_gets_a_numbered_name()
    {
        Write(Staged, "diff.txt", "v1");
        Write(Target, "diff.txt", "edited");
        Write(Target, "diff (restored 2026-10-15 1432).txt", "earlier restore");

        var outcome = KeepBothMerge.Merge(Staged, Target, "2026-10-15 1432");

        Assert.Equal(1, outcome.KeptBoth);
        Assert.Equal("earlier restore", Read(Target, "diff (restored 2026-10-15 1432).txt"));
        Assert.Equal("v1", Read(Target, "diff (restored 2026-10-15 1432) 2.txt"));
    }

    [Fact]
    public void A_file_where_a_folder_should_be_is_kept_and_the_folder_comes_back_beside_it()
    {
        Write(Staged, @"photos\a.jpg", "jpeg");
        Write(Target, "photos", "a file named photos");

        var outcome = KeepBothMerge.Merge(Staged, Target, "2026-10-15 1432");

        Assert.Empty(outcome.Failed);
        Assert.Equal("a file named photos", Read(Target, "photos"));
        Assert.Equal("jpeg", Read(Target, @"photos (restored 2026-10-15 1432)\a.jpg"));
    }

    [Fact]
    public void A_folder_where_a_file_should_be_is_kept_and_the_file_comes_back_beside_it()
    {
        Write(Staged, "notes", "a file");
        Write(Target, @"notes\inside.txt", "inside");

        var outcome = KeepBothMerge.Merge(Staged, Target, "2026-10-15 1432");

        Assert.Equal(1, outcome.KeptBoth);
        Assert.Equal("inside", Read(Target, @"notes\inside.txt"));
        Assert.Equal("a file", Read(Target, "notes (restored 2026-10-15 1432)"));
    }

    [Fact]
    public void A_missing_target_folder_is_created()
    {
        Write(Staged, "a.txt", "a");

        var outcome = KeepBothMerge.Merge(Staged, Path.Combine(Target, "gone", "again"), "2026-10-15 1432");

        Assert.Equal(1, outcome.Moved);
        Assert.Equal("a", Read(Target, @"gone\again\a.txt"));
    }

    [Fact]
    public void Links_inside_the_staged_tree_are_moved_as_links_never_followed()
    {
        var outside = Path.Combine(_root, "outside");
        Write(outside, "precious.txt", "must stay where it is");
        Directory.CreateDirectory(Staged);
        CreateDirectoryLink(Path.Combine(Staged, "link"), outside);

        KeepBothMerge.Merge(Staged, Target, "2026-10-15 1432");

        Assert.Equal("must stay where it is", Read(outside, "precious.txt"));
        Assert.False(File.Exists(Path.Combine(Target, "precious.txt")));
        Assert.NotNull(new DirectoryInfo(Path.Combine(Target, "link")).LinkTarget);
        Directory.Delete(Path.Combine(Target, "link"));
    }

    [Fact]
    public void Describes_what_happened()
    {
        var text = new MergeOutcome { Moved = 3, KeptBoth = 2, Identical = 4 }.Describe(@"C:\Users\pat\Documents", "2026-10-15 1432");

        Assert.Contains(@"C:\Users\pat\Documents", text, StringComparison.Ordinal);
        Assert.Contains("2 files were already there and different", text, StringComparison.Ordinal);
        Assert.Contains("(restored 2026-10-15 1432)", text, StringComparison.Ordinal);
        Assert.Contains("4 identical files", text, StringComparison.Ordinal);
    }

    /// <summary>A symbolic link where allowed (Developer Mode or elevation), else a junction: both are reparse points.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create a junction for the test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));
}
