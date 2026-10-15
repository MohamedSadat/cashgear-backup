using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Contract;

/// <summary>Repository connect, error codes, shutdown and start-up failures: each needs its own server.</summary>
[Trait("Category", "Integration")]
public class RepositoryContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repo_connect_answers_disconnected_then_status_reports_connected()
    {
        using var ws = TestWorkspace.Create(nameof(Repo_connect_answers_disconnected_then_status_reports_connected));
        using var log = new TestLog();
        const string password = "connect-contract-password";

        await using (var creator = ServerHarness.CreateHost(ws, log))
        {
            await creator.StartAsync(null, Ct);
            var client = (await creator.GetSessionAsync(Ct)).Client;
            await client.CreateRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, password, create: true), Ct);
            Assert.True((await ServerHarness.WaitForConnectedAsync(client, Ct)).Connected);
        }

        await using var host = ServerHarness.CreateHost(ws, log);
        await host.StartAsync(null, Ct);
        var connector = (await host.GetSessionAsync(Ct)).Client;
        var (status, body) = await connector.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/repo/connect",
            ServerHarness.Serialize(ServerHarness.FilesystemRepository(ws.Destination, password, create: false)), includeCsrf: true, Ct);

        Assert.Equal(200, status);
        var response = JsonShape.Parse(body);
        JsonShape.Has(response, ("connected", J.Bool));
        Assert.False(response.GetProperty("connected").GetBoolean());
        Assert.True((await ServerHarness.WaitForConnectedAsync(connector, Ct)).Connected);
        Assert.True(host.ConfigExists);
    }

    [Fact]
    public async Task Repo_errors_use_stable_codes()
    {
        using var ws = TestWorkspace.Create(nameof(Repo_errors_use_stable_codes));
        using var log = new TestLog();
        await using (var creator = ServerHarness.CreateHost(ws, log))
        {
            await creator.StartAsync(null, Ct);
            var client = (await creator.GetSessionAsync(Ct)).Client;
            await client.CreateRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, "right-password", create: true), Ct);
            await ServerHarness.WaitForConnectedAsync(client, Ct);
        }

        await using var host = ServerHarness.CreateHost(ws, log);
        await host.StartAsync(null, Ct);
        var api = (await host.GetSessionAsync(Ct)).Client;

        var wrong = await Assert.ThrowsAsync<KopiaApiException>(() =>
            api.ConnectRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, "wrong-password", create: false), Ct));
        Assert.Equal(KopiaApiException.InvalidPassword, wrong.Code);

        var empty = Path.Combine(ws.Root, "empty");
        Directory.CreateDirectory(empty);
        var missing = await Assert.ThrowsAsync<KopiaApiException>(() =>
            api.ConnectRepositoryAsync(ServerHarness.FilesystemRepository(empty, "any", create: false), Ct));
        Assert.Equal(KopiaApiException.NotInitialized, missing.Code);

        var exists = await Assert.ThrowsAsync<KopiaApiException>(() =>
            api.CreateRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, "any", create: true), Ct));
        Assert.Equal(KopiaApiException.AlreadyInitialized, exists.Code);

        var unreachable = await Assert.ThrowsAsync<KopiaApiException>(() =>
            api.CreateRepositoryAsync(ServerHarness.FilesystemRepository(Path.Combine(UnusedDriveRoot(), "repo"), "any", create: true), Ct));
        Assert.Equal(KopiaApiException.StorageConnection, unreachable.Code);

        Assert.False((await api.GetRepoStatusAsync(Ct)).Connected);
        Assert.False(host.ConfigExists);
    }

    [Fact]
    public async Task Control_shutdown_stops_the_process_gracefully()
    {
        using var ws = TestWorkspace.Create(nameof(Control_shutdown_stops_the_process_gracefully));
        using var log = new TestLog();
        await using var host = ServerHarness.CreateHost(ws, log);
        await host.StartAsync(null, Ct);
        var process = host.CurrentProcess!;
        var client = (await host.GetSessionAsync(Ct)).Client;

        var (status, body) = await client.SendRawAsync(KopiaUser.Control, HttpMethod.Post, "/api/v1/control/shutdown", "{}", includeCsrf: false, Ct);
        Assert.Equal(200, status);
        Assert.Equal("{}", body.Trim());

        Assert.True(process.Process.WaitForExit(10_000), "kopia did not exit after control shutdown");
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>A drive letter with nothing mounted: storage that fails fast, unlike an unreachable UNC host.</summary>
    internal static string UnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "ZYXWVUTSRQPONMLKJIHGFE".First(c => !used.Contains(c));
        return $@"{letter}:\";
    }

    [Fact]
    public async Task Server_start_with_a_wrong_repository_password_is_reported_as_InvalidPassword()
    {
        using var ws = TestWorkspace.Create(nameof(Server_start_with_a_wrong_repository_password_is_reported_as_InvalidPassword));
        using var log = new TestLog();
        var id = Guid.NewGuid();
        await using (var creator = ServerHarness.CreateHost(ws, log, repositoryId: id))
        {
            await creator.StartAsync(null, Ct);
            var client = (await creator.GetSessionAsync(Ct)).Client;
            await client.CreateRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, "right-password", create: true), Ct);
            await ServerHarness.WaitForConnectedAsync(client, Ct);
        }

        await using var host = ServerHarness.CreateHost(ws, log, repositoryId: id);
        using var wrong = new SecureSecret("wrong-password");
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => host.StartAsync(wrong, Ct));
        Assert.Equal(BackupErrorKind.InvalidPassword, ex.Kind);
        Assert.Equal(KopiaHostState.Stopped, host.State);
    }
}
