using CashGear.Backup.Core;

namespace CashGear.Backup.Core.Tests;

public class SecureSecretTests
{
    [Fact]
    public void Never_shows_the_value_in_ToString()
    {
        using var secret = new SecureSecret("hunter2-correct-horse");
        Assert.Equal("***", secret.ToString());
        Assert.DoesNotContain("hunter2", $"{secret}", StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_returns_the_value_until_disposed()
    {
        var secret = new SecureSecret("s3cret");
        Assert.Equal("s3cret", secret.Reveal());

        secret.Dispose();
        Assert.True(secret.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => secret.Reveal());
        secret.Dispose(); // idempotent
    }

    [Fact]
    public void Copy_is_independent_of_the_original()
    {
        var original = new SecureSecret("abc");
        using var copy = original.Copy();
        original.Dispose();
        Assert.Equal("abc", copy.Reveal());
    }

    [Fact]
    public void Rejects_an_empty_secret() =>
        Assert.Throws<ArgumentException>(() => new SecureSecret(""));
}

public class RetentionRuleTests
{
    [Fact]
    public void Default_is_valid_and_keeps_the_latest() =>
        Assert.Empty(RetentionRule.Default.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Must_keep_at_least_the_latest_snapshot(int keepLatest)
    {
        var errors = new RetentionRule { KeepLatest = keepLatest }.Validate();
        Assert.Contains(errors, e => e.Contains("KeepLatest >= 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_negative_and_absurd_counts()
    {
        var errors = new RetentionRule { KeepDaily = -1, KeepAnnual = RetentionRule.MaxCount + 1 }.Validate();
        Assert.Equal(2, errors.Count);
    }
}

public class BackupJobTests
{
    private static BackupJob Valid() => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        Name = "Documents",
        SourcePath = @"C:\Users\someone\Documents",
    };

    [Fact]
    public void A_complete_job_is_valid() => Assert.Empty(Valid().Validate());

    [Theory]
    [InlineData("")]
    [InlineData("Documents")]
    [InlineData(@"\Documents")]
    [InlineData("C:Documents")]
    public void Source_must_be_an_absolute_path(string source) =>
        Assert.NotEmpty((Valid() with { SourcePath = source }).Validate());

    [Fact]
    public void Excludes_must_be_single_non_empty_lines() =>
        Assert.NotEmpty((Valid() with { Excludes = ["*.tmp", "a\nb"] }).Validate());

    [Fact]
    public void Invalid_retention_makes_the_job_invalid() =>
        Assert.NotEmpty((Valid() with { Retention = new RetentionRule { KeepLatest = 0 } }).Validate());

    [Fact]
    public void ThrowIfInvalid_reports_InvalidRequest()
    {
        var ex = Assert.Throws<BackupEngineException>(() =>
            BackupEngineException.ThrowIfInvalid((Valid() with { Id = Guid.Empty }).Validate(), "job"));
        Assert.Equal(BackupErrorKind.InvalidRequest, ex.Kind);
    }
}

public class RestoreRequestTests
{
    private static readonly SnapshotInfo s_snapshot = new()
    {
        Id = "s1",
        RepositoryId = Guid.NewGuid(),
        SourcePath = @"C:\data",
        StartTime = DateTimeOffset.UnixEpoch,
        EndTime = DateTimeOffset.UnixEpoch,
        RootId = "k0123",
    };

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a")]
    [InlineData("a/b/c.txt")]
    [InlineData(@"a\b")]
    public void Accepts_relative_paths_inside_the_snapshot(string path) =>
        Assert.Empty(new RestoreRequest { Snapshot = s_snapshot, PathInSnapshot = path, TargetFolder = @"C:\restore" }.Validate());

    [Theory]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a//b")]
    [InlineData(@"C:\a")]
    [InlineData("//server/share")]
    public void Rejects_paths_that_escape_or_are_ambiguous(string path) =>
        Assert.NotEmpty(new RestoreRequest { Snapshot = s_snapshot, PathInSnapshot = path, TargetFolder = @"C:\restore" }.Validate());

    [Fact]
    public void Target_must_be_absolute() =>
        Assert.NotEmpty(new RestoreRequest { Snapshot = s_snapshot, TargetFolder = "restore" }.Validate());
}

public class DestinationTests
{
    [Fact]
    public void File_system_destination_needs_an_id_and_an_absolute_path()
    {
        Assert.Empty(new FileSystemDestination(Guid.NewGuid(), @"\\nas\backups\pc1").Validate());
        Assert.Equal(2, new FileSystemDestination(Guid.Empty, "backups").Validate().Count);
    }
}
