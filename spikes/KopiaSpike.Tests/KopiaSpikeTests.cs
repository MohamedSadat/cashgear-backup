using CashGear.Backup.Testing;

namespace KopiaSpike.Tests;

/// <summary>Runs the whole spike scenario once (one server, one repository) for all tests below.</summary>
public sealed class SpikeRunFixture : IAsyncLifetime
{
    public SpikeReport Report { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new SpikeOptions(
            KopiaTestBinary.ExePath,
            KopiaTestBinary.Pin.Version,
            KeepWorkspace: false,
            RepoPaths.Combine("artifacts", "spike"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Report = await SpikeScenario.RunAsync(options, cts.Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Trait("Category", "Integration")]
public class KopiaSpikeTests(SpikeRunFixture fixture) : IClassFixture<SpikeRunFixture>
{
    [Fact]
    public void Restore_of_the_first_snapshot_is_byte_identical() =>
        AssertChecks("repo.create", "source.registered", "backup.1", "restore.1.task", "roundtrip.snapshot1");

    [Fact]
    public void Incremental_backup_and_point_in_time_restore_are_byte_identical() =>
        AssertChecks("backup.2", "roundtrip.latest", "roundtrip.point-in-time");

    [Fact]
    public void Restore_never_overwrites_an_existing_folder() =>
        AssertChecks("restore.never-overwrites");

    [Fact]
    public void Server_stops_gracefully_and_restarts_with_the_password_from_the_environment() =>
        AssertChecks("shutdown.graceful", "restart.with-env-password");

    [Fact]
    public void Repository_password_is_never_persisted_or_leaked() =>
        AssertChecks("secrets.no-password-file", "secrets.no-credential-manager-entry", "secrets.no-leaks");

    [Fact]
    public void Api_requires_csrf_the_right_user_and_the_pinned_certificate() =>
        AssertChecks("csrf.required", "csrf.accepted", "auth.control-user-cannot-use-ui-api", "tls.pin-mismatch-rejected");

    private void AssertChecks(params string[] names)
    {
        var report = fixture.Report;
        Assert.True(report.Fatal is null, $"Spike aborted: {report.Fatal} (artifacts: {report.ArtifactsDirectory})");
        foreach (var name in names)
        {
            var check = report.Find(name);
            Assert.True(check is not null, $"Check '{name}' was never reached (artifacts: {report.ArtifactsDirectory}).");
            Assert.True(check.Passed, $"{name}: {check.Detail}");
        }
    }
}
