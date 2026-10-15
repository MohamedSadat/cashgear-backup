using System.Diagnostics;
using System.Text.Json;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

/// <summary>A <see cref="KopiaProcessHost"/> on the pinned kopia.exe, below the engine, for contract and host tests.</summary>
internal static class ServerHarness
{
    public const string ClientUser = "cashgear";
    public const string ClientHost = "contract-host";
    public static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(2);

    public static KopiaProcessHost CreateHost(TestWorkspace ws, TestLog log, string? exePath = null, Guid? repositoryId = null,
        Func<KopiaProcessHostOptions, KopiaProcessHostOptions>? configure = null)
    {
        var html = Path.Combine(ws.Data, "html");
        Directory.CreateDirectory(html);
        File.WriteAllText(Path.Combine(html, "index.html"), "<!doctype html><html><head><title>CashGear</title></head><body></body></html>");
        var id = repositoryId ?? Guid.NewGuid();
        var options = new KopiaProcessHostOptions
        {
            KopiaExePath = exePath ?? KopiaTestBinary.ExePath,
            Layout = KopiaRepositoryLayout.For(ws.Data, id),
            HtmlDirectory = html,
            Name = id.ToString("D"),
        };
        return new KopiaProcessHost(configure is null ? options : configure(options), log.Factory);
    }

    public static RepositoryRequest FilesystemRepository(string path, string password, bool create) =>
        new(new StorageConnection("filesystem", new() { ["path"] = path }), password, new ClientOptions(ClientHost, ClientUser, "CashGear tests"))
        {
            Options = create ? [] : null,
        };

    public static string Serialize(object body) => JsonSerializer.Serialize(body, body.GetType(), KopiaApiClient.Json);

    public static async Task<RepoStatus> WaitForConnectedAsync(KopiaApiClient client, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var status = await client.GetRepoStatusAsync(ct);
            if (status.Connected || stopwatch.Elapsed > TimeSpan.FromSeconds(30))
            {
                return status;
            }

            await Task.Delay(100, ct);
        }
    }

    public static async Task<TaskInfo> WaitForTaskAsync(KopiaApiClient client, string id, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var task = await client.GetTaskAsync(id, ct);
            if (task.IsFinished)
            {
                return task;
            }

            Assert.True(stopwatch.Elapsed < TaskTimeout, $"task {id} did not finish within {TaskTimeout}");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Triggers an upload and waits for its task (found through the source's currentTask or the task list).</summary>
    public static async Task<TaskInfo> UploadAndWaitAsync(KopiaApiClient client, SourceInfo source, CancellationToken ct)
    {
        var known = (await client.ListTasksAsync(ct)).Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        await client.StartUploadAsync(source, ct);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var task = (await client.ListTasksAsync(ct)).Tasks.FirstOrDefault(t =>
                t.Kind == TaskInfo.KindSnapshot && !known.Contains(t.Id) && t.Description?.StartsWith(source + " at ", StringComparison.Ordinal) == true);
            if (task is not null)
            {
                return await WaitForTaskAsync(client, task.Id, ct);
            }

            Assert.True(stopwatch.Elapsed < TaskTimeout, "the snapshot task never appeared");
            await Task.Delay(100, ct);
        }
    }
}
