using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Engine;

[Trait("Category", "Integration")]
public class BrowseTests
{
    private static CancellationToken Ct => EngineHarness.Ct;

    /// <summary>Kopia's directory object for an empty folder has <c>"entries": null</c> (seen in Phase 3).</summary>
    [Fact]
    public async Task An_empty_folder_browses_as_no_entries()
    {
        await using var h = await EngineHarness.CreateAsync(nameof(An_empty_folder_browses_as_no_entries));
        TreeFixture.Create(h.Workspace.Source);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(h.Workspace.Source, "empty-dir")));
        var job = h.Job();
        await h.BackupAsync(job);
        var snapshot = (await h.Engine.ListSnapshotsAsync(job, Ct))[0];

        var root = await h.Engine.BrowseAsync(snapshot, "", Ct);
        var empty = await h.Engine.BrowseAsync(snapshot, "empty-dir", Ct);

        Assert.Equal(SnapshotEntryKind.Directory, root.Single(e => e.Name == "empty-dir").Kind);
        Assert.Empty(empty);
    }
}
