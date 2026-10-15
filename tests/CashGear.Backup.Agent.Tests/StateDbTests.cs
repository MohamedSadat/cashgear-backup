using System.Security.Cryptography;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CashGear.Backup.Agent.Tests;

public sealed class StateDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cashgear-statedb-tests", RandomNumberGenerator.GetHexString(8, lowercase: true));

    public StateDbTests() => Directory.CreateDirectory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_create_the_schema_and_match_the_model()
    {
        await using var db = Open();
        await StateDb.MigrateAsync(db, Ct);

        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Ct), m => m.EndsWith("_InitialState", StringComparison.Ordinal));
        Assert.False(db.Database.HasPendingModelChanges(), "the model changed without a migration: run dotnet ef migrations add");
        Assert.Equal("wal", (await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync(Ct)).Single(), ignoreCase: true);
    }

    [Fact]
    public async Task Records_round_trip_and_runs_order_by_time()
    {
        var destination = Destination();
        var job = Job(destination.Id, @"C:\Data");
        var older = new RunRecord
        {
            Id = Guid.NewGuid(), JobId = job.Id, Kind = RunKind.Backup, State = RunState.CompletedWithWarnings, Trigger = RunTrigger.Scheduled,
            StartedAt = new DateTimeOffset(2026, 10, 14, 2, 0, 0, TimeSpan.FromHours(3)),
            FinishedAt = new DateTimeOffset(2026, 10, 14, 2, 5, 0, TimeSpan.FromHours(3)),
            SnapshotId = "abc", Files = 3, Bytes = 42, FailedItemCount = 1,
            FailedItems = [new FailedItemRecord { Path = "locked.txt", Error = "access denied" }],
        };
        var newer = new RunRecord
        {
            Id = Guid.NewGuid(), JobId = job.Id, Kind = RunKind.Backup, State = RunState.Running, Trigger = RunTrigger.Manual,
            StartedAt = new DateTimeOffset(2026, 10, 14, 0, 30, 0, TimeSpan.Zero), // later instant, earlier wall clock
        };

        await using (var db = await MigratedAsync())
        {
            db.AddRange(destination, job, older, newer);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Open())
        {
            var runs = await db.Runs.Where(r => r.JobId == job.Id).OrderByDescending(r => r.StartedAt).ToListAsync(Ct);
            Assert.Equal([newer.Id, older.Id], runs.Select(r => r.Id));
            var loaded = runs[1];
            Assert.Equal(older.StartedAt, loaded.StartedAt);
            Assert.Equal(RunState.CompletedWithWarnings, loaded.State);
            Assert.Equal("locked.txt", Assert.Single(loaded.FailedItems).Path);

            var loadedJob = await db.Jobs.SingleAsync(Ct);
            Assert.Equal(["*.tmp", "cache/"], loadedJob.Excludes);
            Assert.Equal(BackupCompression.Smallest, loadedJob.Compression);
            Assert.Equal(destination.ProtectedPassword, (await db.Destinations.SingleAsync(Ct)).ProtectedPassword);
        }
    }

    [Fact]
    public async Task One_source_folder_per_destination_whatever_its_case()
    {
        var destination = Destination();
        await using var db = await MigratedAsync();
        db.AddRange(destination, Job(destination.Id, @"C:\Data\Photos"));
        await db.SaveChangesAsync(Ct);

        db.Add(Job(destination.Id, @"c:\data\PHOTOS"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StateDbContext Open()
    {
        var options = new DbContextOptionsBuilder<StateDbContext>();
        options.UseStateDb(Path.Combine(_dir, StateDb.FileName));
        return new StateDbContext(options.Options);
    }

    private async Task<StateDbContext> MigratedAsync()
    {
        var db = Open();
        await StateDb.MigrateAsync(db, Ct);
        return db;
    }

    private static DestinationRecord Destination() => new()
    {
        Id = Guid.NewGuid(),
        Name = "disk",
        Kind = DestinationKind.FileSystem,
        Location = @"E:\Backups",
        ProtectedPassword = [1, 2, 3],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static JobRecord Job(Guid destinationId, string source) => new()
    {
        Id = Guid.NewGuid(),
        Name = "job",
        DestinationId = destinationId,
        SourcePath = source,
        KeepLatest = 5,
        Excludes = ["*.tmp", "cache/"],
        Compression = BackupCompression.Smallest,
        ScheduleKind = ScheduleKind.Daily,
        ScheduleMinuteOfDay = 120,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
