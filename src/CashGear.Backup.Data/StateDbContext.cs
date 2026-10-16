using CashGear.Backup.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CashGear.Backup.Data;

/// <summary>The agent's state.db.</summary>
public sealed class StateDbContext(DbContextOptions<StateDbContext> options) : DbContext(options)
{
    public DbSet<DestinationRecord> Destinations => Set<DestinationRecord>();
    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare or order DateTimeOffset stored as text. EF's DateTimeOffsetToBinaryConverter
        // encodes local wall-clock ticks, which orders by wall clock, not by instant; UTC ticks order correctly.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<RunKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RunState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RunTrigger>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<BackupCompression>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ScheduleKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<DestinationKind>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DestinationRecord>(d =>
        {
            d.ToTable("Destinations");
            d.Property(x => x.Name).HasMaxLength(200);
            d.Property(x => x.Location).HasMaxLength(1024).UseCollation("NOCASE");
            d.HasIndex(x => x.Location).IsUnique();
        });

        modelBuilder.Entity<JobRecord>(j =>
        {
            j.ToTable("Jobs");
            j.Property(x => x.Name).HasMaxLength(200);
            j.Property(x => x.SourcePath).HasMaxLength(1024).UseCollation("NOCASE");

            // Two jobs on one folder in one repository would share the engine's snapshot history and prune each other's snapshots.
            j.HasIndex(x => new { x.DestinationId, x.SourcePath }).IsUnique();
            j.HasOne<DestinationRecord>().WithMany().HasForeignKey(x => x.DestinationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RunRecord>(r =>
        {
            r.ToTable("Runs");
            r.HasIndex(x => new { x.JobId, x.StartedAt });
            r.HasIndex(x => x.State);
            r.Property(x => x.SnapshotId).HasMaxLength(128);
            r.OwnsMany(x => x.FailedItems, f => f.ToJson());
        });

        modelBuilder.Entity<SettingRecord>(s =>
        {
            s.ToTable("Settings");
            s.HasKey(x => x.Key);
            s.Property(x => x.Key).HasMaxLength(100);
        });
    }
}

/// <summary>Stores a <see cref="DateTimeOffset"/> as UTC ticks; it comes back with offset zero (same instant).</summary>
internal sealed class DateTimeOffsetToUtcTicksConverter()
    : ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

/// <summary>Opening and upgrading state.db.</summary>
public static class StateDb
{
    public const string FileName = "state.db";

    /// <summary>Failed items kept per run; the count is always exact.</summary>
    public const int MaxStoredFailedItems = 200;

    public static DbContextOptionsBuilder UseStateDb(this DbContextOptionsBuilder builder, string path) =>
        builder.UseSqlite($"Data Source={path}");

    /// <summary>Applies pending migrations and switches the database to WAL, so readers never block the writer.</summary>
    public static async Task MigrateAsync(StateDbContext db, CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}

/// <summary>Lets <c>dotnet ef</c> build the model; never used at run time.</summary>
internal sealed class DesignTimeStateDbContextFactory : IDesignTimeDbContextFactory<StateDbContext>
{
    public StateDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<StateDbContext>();
        builder.UseStateDb(Path.Combine(Path.GetTempPath(), "cashgear-design-time.db"));
        return new StateDbContext(builder.Options);
    }
}
