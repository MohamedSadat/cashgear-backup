using System.Security.Cryptography;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Engine.Kopia.Policy;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Contract;

/// <summary>
/// One kopia server with a created repository, a registered source carrying our policy, and one snapshot,
/// shared by the contract tests. Raw responses of the one-shot calls are kept for shape checks.
/// </summary>
public sealed class ContractServerFixture : IAsyncLifetime
{
    internal TestWorkspace Workspace { get; private set; } = null!;
    internal TestLog Log { get; private set; } = null!;
    internal KopiaProcessHost Host { get; private set; } = null!;
    internal KopiaApiClient Client { get; private set; } = null!;
    internal string Password { get; } = RandomNumberGenerator.GetHexString(32);
    internal string CreateResponse { get; private set; } = null!;
    internal string CreateSourceResponse { get; private set; } = null!;
    internal string UploadResponse { get; private set; } = null!;
    internal SourceInfo Source { get; private set; } = null!;
    internal BackupJob Job { get; private set; } = null!;
    internal TaskInfo SnapshotTask { get; private set; } = null!;
    internal SnapshotItem Snapshot { get; private set; } = null!;
    internal TreeManifest Manifest { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        Workspace = TestWorkspace.Create("contract");
        Log = new TestLog();
        TreeFixture.Create(Workspace.Source);
        Manifest = TreeManifest.Capture(Workspace.Source);

        Host = ServerHarness.CreateHost(Workspace, Log);
        await Host.StartAsync(null, ct);
        Client = (await Host.GetSessionAsync(ct)).Client;

        var create = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/repo/create",
            ServerHarness.Serialize(ServerHarness.FilesystemRepository(Workspace.Destination, Password, create: true)), includeCsrf: true, ct);
        Assert.Equal(200, create.Status);
        CreateResponse = create.Body;
        Assert.True((await ServerHarness.WaitForConnectedAsync(Client, ct)).Connected);
        using (var secret = new SecureSecret(Password))
        {
            Host.SetRepositoryPassword(secret);
        }

        Source = new SourceInfo(ServerHarness.ClientHost, ServerHarness.ClientUser, Workspace.Source);
        Job = new BackupJob { Id = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), Name = "contract", SourcePath = Workspace.Source, Excludes = ["*.tmp"] };
        var createSource = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources",
            ServerHarness.Serialize(new CreateSourceRequest(Source.Path, false, KopiaPolicyMapper.ToPolicy(Job))), includeCsrf: true, ct);
        Assert.Equal(200, createSource.Status);
        CreateSourceResponse = createSource.Body;

        var known = (await Client.ListTasksAsync(ct)).Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var upload = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources/upload?" + Source.ToQuery(), "{}", includeCsrf: true, ct);
        Assert.Equal(200, upload.Status);
        UploadResponse = upload.Body;

        TaskInfo? task = null;
        while (task is null)
        {
            task = (await Client.ListTasksAsync(ct)).Tasks.FirstOrDefault(t => t.Kind == TaskInfo.KindSnapshot && !known.Contains(t.Id));
            await Task.Delay(100, ct);
        }

        SnapshotTask = await ServerHarness.WaitForTaskAsync(Client, task.Id, ct);
        Snapshot = Assert.Single((await Client.ListSnapshotsAsync(Source, ct)).Snapshots);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Log.Dispose();
        Workspace.Dispose();
    }
}
