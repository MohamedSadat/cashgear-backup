using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Engine.Kopia.Policy;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Contract;

/// <summary>
/// One test per REST endpoint the engine uses, against the pinned kopia.exe: the request shapes we send are
/// accepted and the response fields we read exist with the types we expect. A Kopia bump that breaks any of
/// this fails here, not at a customer.
/// </summary>
[Trait("Category", "Integration")]
public class KopiaApiContractTests(ContractServerFixture fx) : IClassFixture<ContractServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KopiaApiClient Client => fx.Client;

    [Fact]
    public async Task Get_root_serves_our_stub_page_with_a_csrf_token()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/", null, includeCsrf: false, Ct);
        Assert.Equal(200, status);
        Assert.Contains("<title>CashGear</title>", body, StringComparison.Ordinal);
        Assert.Matches("<meta name=\"kopia-csrf-token\" content=\"[0-9a-f]{32,}\"", body);
    }

    [Fact]
    public async Task Ui_calls_need_the_csrf_token_and_the_client_refetches_it_once()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, includeCsrf: false, Ct);
        Assert.Equal(401, status);
        Assert.Contains("CSRF", body, StringComparison.OrdinalIgnoreCase);

        Client.CorruptCsrfTokenForTest();
        Assert.True((await Client.GetRepoStatusAsync(Ct)).Connected);
    }

    [Fact]
    public async Task Control_status_answers_the_control_user_and_not_the_ui_user()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Control, HttpMethod.Get, "/api/v1/control/status", null, includeCsrf: false, Ct);
        Assert.Equal(200, status);
        JsonShape.Has(JsonShape.Parse(body), ("connected", J.Bool));

        var asUi = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/control/status", null, includeCsrf: true, Ct);
        Assert.InRange(asUi.Status, 401, 403);
    }

    [Fact]
    public async Task Tls_from_a_certificate_other_than_the_pinned_one_is_rejected()
    {
        var credentials = fx.Host.CurrentCredentials!;
        using var client = KopiaApiClient.CreateHttpClient(Client.BaseAddress, RandomNumberGenerator.GetBytes(32), credentials.ControlUser, credentials.ControlPassword, useCookies: false);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/v1/control/status", Ct));
        Assert.IsType<AuthenticationException>(ex.InnerException);
    }

    [Fact]
    public async Task Repo_create_answers_disconnected_and_status_then_reports_connected()
    {
        // FINDINGS #1: the create response is the status from before connecting.
        var created = JsonShape.Parse(fx.CreateResponse);
        JsonShape.Has(created, ("connected", J.Bool));
        Assert.False(created.GetProperty("connected").GetBoolean());

        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var json = JsonShape.Parse(body);
        JsonShape.Has(json, ("connected", J.Bool), ("encryption", J.String), ("hash", J.String), ("storage", J.String),
            ("supportsContentCompression", J.Bool), ("hostname", J.String), ("username", J.String));
        Assert.True(json.GetProperty("connected").GetBoolean());
        Assert.Equal("filesystem", json.GetProperty("storage").GetString());
        Assert.Equal(ServerHarness.ClientHost, json.GetProperty("hostname").GetString());
        Assert.Equal(ServerHarness.ClientUser, json.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Algorithms_offer_every_compressor_we_write()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/algorithms", null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        JsonShape.Has(JsonShape.Parse(body), ("compression", J.Array), ("encryption", J.Array), ("hash", J.Array));

        var algorithms = await Client.GetAlgorithmsAsync(Ct);
        foreach (var compression in Enum.GetValues<Core.BackupCompression>().Where(c => c != Core.BackupCompression.Off))
        {
            var name = KopiaPolicyMapper.CompressorName(compression);
            var option = algorithms.Compression.SingleOrDefault(a => a.Id == name);
            Assert.True(option is { Deprecated: false }, $"compressor '{name}' is missing or deprecated in Kopia {KopiaTestBinary.Pin.Version}");
        }
    }

    [Fact]
    public async Task Sources_list_reports_our_identity_and_source()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/sources", null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var json = JsonShape.Parse(body);
        JsonShape.Has(json, ("localUsername", J.String), ("localHost", J.String), ("sources", J.Array));
        var ours = json.GetProperty("sources").EnumerateArray().Single(s => JsonShape.Get(s, "source.path").GetString() == fx.Source.Path);
        JsonShape.Has(ours, ("source", J.Object), ("status", J.String));
        JsonShape.Has(ours.GetProperty("source"), ("host", J.String), ("userName", J.String), ("path", J.String));

        var typed = await Client.ListSourcesAsync(Ct);
        Assert.Equal(ServerHarness.ClientUser, typed.LocalUsername);
        Assert.Equal(ServerHarness.ClientHost, typed.LocalHost);
        Assert.Contains(typed.Sources, s => s.Source == fx.Source);
    }

    [Fact]
    public void Sources_create_accepts_our_policy()
    {
        Assert.Equal(JsonValueKind.Object, JsonShape.Parse(fx.CreateSourceResponse).ValueKind);
    }

    [Fact]
    public async Task Policy_get_returns_exactly_what_we_wrote()
    {
        var stored = await Client.GetPolicyAsync(fx.Source, Ct);
        Assert.NotNull(stored);
        Assert.Empty(KopiaPolicyMapper.Differences(KopiaPolicyMapper.ToPolicy(fx.Job), stored));
        Assert.Equal(VolumeShadowCopyPolicyDto.WhenAvailable, JsonShape.Get(stored.Value, "osSnapshots.volumeShadowCopy.enable").GetInt32());
    }

    [Fact]
    public async Task Policy_put_answers_ok_for_our_policy()
    {
        var (status, _) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Put, "/api/v1/policy?" + fx.Source.ToQuery(),
            ServerHarness.Serialize(KopiaPolicyMapper.ToPolicy(fx.Job)), includeCsrf: true, Ct);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Policy_resolve_shows_our_values_are_the_effective_ones()
    {
        var stored = (await Client.GetPolicyAsync(fx.Source, Ct))!.Value;
        var resolved = await Client.ResolvePolicyAsync(fx.Source, new ResolvePolicyRequest(stored), Ct);
        var effective = resolved.Effective;
        var expected = KopiaPolicyMapper.ToPolicy(fx.Job);

        Assert.Equal(expected.Retention.KeepLatest, JsonShape.Get(effective, "retention.keepLatest").GetInt32());
        Assert.Equal(expected.Retention.KeepHourly, JsonShape.Get(effective, "retention.keepHourly").GetInt32());
        Assert.Equal(expected.Retention.KeepAnnual, JsonShape.Get(effective, "retention.keepAnnual").GetInt32());
        Assert.False(JsonShape.Get(effective, "retention.ignoreIdenticalSnapshots").GetBoolean());
        Assert.Equal(expected.Compression.CompressorName, JsonShape.Get(effective, "compression.compressorName").GetString());
        Assert.True(JsonShape.Get(effective, "scheduling.manual").GetBoolean());
        Assert.Equal(VolumeShadowCopyPolicyDto.WhenAvailable, JsonShape.Get(effective, "osSnapshots.volumeShadowCopy.enable").GetInt32());
        Assert.Equal([KopiaPolicyMapper.IgnoreFileName], JsonShape.Get(effective, "files.ignoreDotFiles").EnumerateArray().Select(e => e.GetString()));
        Assert.False(JsonShape.Get(effective, "errorHandling.ignoreFileErrors").GetBoolean());
    }

    [Fact]
    public async Task Policy_put_replaces_the_whole_policy_and_drops_unknown_fields()
    {
        // Why the engine always writes every section and reads it back (rule 8).
        var other = fx.Source with { Path = Path.Combine(fx.Workspace.Root, "policy-probe") };
        await Client.SetPolicyAsync(other, KopiaPolicyMapper.ToPolicy(fx.Job), Ct);

        var partial = """{"retention":{"keepLatest":2,"keepLatst":9},"bogus":1}""";
        var (status, _) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Put, "/api/v1/policy?" + other.ToQuery(), partial, includeCsrf: true, Ct);
        Assert.Equal(200, status);

        var stored = (await Client.GetPolicyAsync(other, Ct))!.Value;
        Assert.Equal(2, JsonShape.Get(stored, "retention.keepLatest").GetInt32());
        Assert.False(stored.GetProperty("retention").TryGetProperty("keepDaily", out _), "fields not sent are cleared");
        Assert.False(stored.GetProperty("retention").TryGetProperty("keepLatst", out _), "unknown fields are dropped without an error");
        Assert.False(stored.GetProperty("compression").TryGetProperty("compressorName", out _));
    }

    [Fact]
    public void Upload_answers_with_only_a_per_source_success_flag()
    {
        var json = JsonShape.Parse(fx.UploadResponse);
        JsonShape.Has(json, ("sources", J.Object));
        var entry = json.GetProperty("sources").GetProperty(fx.Source.ToString());
        JsonShape.Has(entry, ("success", J.Bool));
        Assert.True(entry.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Snapshots_list_reports_ids_roots_and_summaries()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/snapshots?all=1&" + fx.Source.ToQuery(), null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var json = JsonShape.Parse(body);
        JsonShape.Has(json, ("snapshots", J.Array));
        var snapshot = Assert.Single(json.GetProperty("snapshots").EnumerateArray());
        JsonShape.Has(snapshot, ("id", J.String), ("startTime", J.String), ("endTime", J.String), ("rootID", J.String), ("summary", J.Object));
        JsonShape.Has(snapshot.GetProperty("summary"), ("size", J.Number), ("files", J.Number), ("dirs", J.Number), ("numFailed", J.Number));
        Assert.False(snapshot.TryGetProperty("incomplete", out _));

        Assert.Equal(fx.Manifest.FileCount, fx.Snapshot.Summary!.Files);
        Assert.Equal(fx.Manifest.TotalBytes, fx.Snapshot.Summary.Size);
        Assert.True(KopiaApiClient.IsValidObjectId(fx.Snapshot.RootId));
    }

    [Fact]
    public async Task Tasks_list_and_get_report_kind_status_and_counters()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks", null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var listed = JsonShape.Parse(body).GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("id").GetString() == fx.SnapshotTask.Id);
        JsonShape.Has(listed, ("id", J.String), ("kind", J.String), ("status", J.String), ("startTime", J.String), ("description", J.String));

        (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks/" + fx.SnapshotTask.Id, null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var task = JsonShape.Parse(body);
        JsonShape.Has(task, ("kind", J.String), ("status", J.String), ("counters", J.Object), ("endTime", J.String));
        Assert.Equal(TaskInfo.KindSnapshot, task.GetProperty("kind").GetString());
        Assert.Equal(TaskInfo.StatusSuccess, task.GetProperty("status").GetString());
        Assert.StartsWith(fx.Source + " at ", task.GetProperty("description").GetString(), StringComparison.Ordinal);

        // The progress counters the engine reports (never "Uploaded Bytes", FINDINGS #5).
        foreach (var counter in new[] { "Processed Files", "Processed Bytes", "Hashed Files", "Hashed Bytes", "Errors" })
        {
            JsonShape.Has(task.GetProperty("counters").GetProperty(counter), ("value", J.Number));
        }

        Assert.Equal(fx.Manifest.FileCount, fx.SnapshotTask.Counter("Processed Files"));
    }

    [Fact]
    public async Task Objects_returns_the_directory_manifest_of_a_snapshot_root()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/api/v1/objects/" + fx.Snapshot.RootId, null, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var json = JsonShape.Parse(body);
        JsonShape.Has(json, ("entries", J.Array));
        var file = json.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "empty.txt");
        JsonShape.Has(file, ("name", J.String), ("type", J.String), ("mtime", J.String), ("obj", J.String));
        var dir = json.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "many");
        JsonShape.Has(dir, ("type", J.String), ("obj", J.String), ("summ", J.Object));

        var typed = await Client.GetDirectoryAsync(fx.Snapshot.RootId, Ct);
        var many = typed.Entries.Single(e => e.Name == "many");
        Assert.Equal(DirectoryEntry.TypeDirectory, many.Type);
        var listing = await Client.GetDirectoryAsync(many.ObjectId!, Ct);
        Assert.Equal(200, listing.Entries.Count);
        Assert.All(listing.Entries, e => Assert.Equal(DirectoryEntry.TypeFile, e.Type));
        Assert.Equal(new FileInfo(Path.Combine(fx.Workspace.Source, "many", "file-000.txt")).Length, listing.Entries.Single(e => e.Name == "file-000.txt").Size);
    }

    [Theory]
    [InlineData("kdeadbeef")]
    [InlineData("k0123/sub")]
    [InlineData("../x")]
    [InlineData("")]
    public async Task Objects_with_malformed_ids_are_rejected_before_reaching_kopia(string id)
    {
        // A malformed ID makes Kopia's handler panic and drop the connection.
        await Assert.ThrowsAsync<ArgumentException>(() => Client.GetDirectoryAsync(id, Ct));
    }

    [Fact]
    public async Task Restore_starts_a_task_that_restores_every_file_at_full_depth()
    {
        var target = fx.Workspace.RestoreTarget("contract-full-" + Guid.NewGuid().ToString("N")[..6]);
        var request = new RestoreRequestDto(fx.Snapshot.RootId, new FilesystemOutput(target), new RestoreOptions());
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/restore", ServerHarness.Serialize(request), includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var started = JsonShape.Parse(body);
        JsonShape.Has(started, ("id", J.String), ("kind", J.String), ("status", J.String));
        Assert.Equal(TaskInfo.KindRestore, started.GetProperty("kind").GetString());

        var task = await ServerHarness.WaitForTaskAsync(Client, started.GetProperty("id").GetString()!, Ct);
        Assert.Equal(TaskInfo.StatusSuccess, task.Status);
        Assert.Equal(fx.Manifest.FileCount, task.Counter("Restored Files"));
        EngineHarness.AssertIdentical(fx.Manifest, target);
        EngineHarness.AssertNoPlaceholders(target);
    }

    [Fact]
    public async Task Restore_into_a_non_empty_folder_fails_and_changes_nothing()
    {
        var target = fx.Workspace.RestoreTarget("contract-nonempty-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "user data", Ct);
        var before = TreeManifest.Capture(target);

        var started = await Client.StartRestoreAsync(new RestoreRequestDto(fx.Snapshot.RootId, new FilesystemOutput(target), new RestoreOptions()), Ct);
        var task = await ServerHarness.WaitForTaskAsync(Client, started.Id, Ct);

        Assert.Equal(TaskInfo.StatusFailed, task.Status);
        Assert.Contains("not overwriting", task.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(before.ContentDifferences(TreeManifest.Capture(target)));
    }

    [Fact]
    public async Task Kopia_default_restore_depth_writes_placeholders_which_is_why_we_always_send_it()
    {
        var target = fx.Workspace.RestoreTarget("contract-shallow-" + Guid.NewGuid().ToString("N")[..6]);
        var withoutDepth = $$$"""{"root":"{{{fx.Snapshot.RootId}}}","fsOutput":{"targetPath":{{{JsonSerializer.Serialize(target)}}}},"options":{}}""";
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/restore", withoutDepth, includeCsrf: true, Ct);
        Assert.Equal(200, status);
        var task = await ServerHarness.WaitForTaskAsync(Client, JsonShape.Parse(body).GetProperty("id").GetString()!, Ct);

        Assert.Equal(TaskInfo.StatusSuccess, task.Status);
        Assert.NotEmpty(Directory.GetFiles(target, "*.kopia-entry", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Task_cancel_answers_ok()
    {
        var target = fx.Workspace.RestoreTarget("contract-cancel-" + Guid.NewGuid().ToString("N")[..6]);
        var started = await Client.StartRestoreAsync(new RestoreRequestDto(fx.Snapshot.RootId, new FilesystemOutput(target), new RestoreOptions()), Ct);
        var (status, _) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, $"/api/v1/tasks/{started.Id}/cancel", "{}", includeCsrf: true, Ct);
        Assert.Equal(200, status);

        var task = await ServerHarness.WaitForTaskAsync(Client, started.Id, Ct);
        Assert.Contains(task.Status, new[] { TaskInfo.StatusCanceled, TaskInfo.StatusSuccess });
    }

    [Fact]
    public async Task Sources_cancel_answers_with_a_per_source_success_flag()
    {
        var (status, body) = await Client.SendRawAsync(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources/cancel?" + fx.Source.ToQuery(), "{}", includeCsrf: true, Ct);
        Assert.Equal(200, status);
        JsonShape.Has(JsonShape.Get(JsonShape.Parse(body), "sources").GetProperty(fx.Source.ToString()), ("success", J.Bool));
    }

    [Fact]
    public async Task Metrics_are_not_served_without_credentials()
    {
        using var anonymous = KopiaApiClient.CreateHttpClient(Client.BaseAddress, fx.Host.CurrentCertificatePinForTest(), null, null, useCookies: false);
        using var response = await anonymous.GetAsync("/metrics", Ct);
        Assert.Equal(401, (int)response.StatusCode);

        using var wrong = KopiaApiClient.CreateHttpClient(Client.BaseAddress, fx.Host.CurrentCertificatePinForTest(), "ui-x", "nope", useCookies: false);
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("x:y")));
        using var denied = await wrong.GetAsync("/api/v1/control/status", Ct);
        Assert.Equal(401, (int)denied.StatusCode);
    }
}
