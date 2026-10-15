using System.Text.Json;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Api;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Engine.Kopia.Policy;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Unit;

/// <summary>What we send to Kopia, checked without Kopia: policy mapping, restore flags, launch line, environment.</summary>
public class RequestShapeTests
{
    private static BackupJob Job() => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        Name = "docs",
        SourcePath = @"C:\Users\someone\Documents",
        Excludes = ["*.tmp", "node_modules/"],
        Compression = BackupCompression.Smallest,
        Retention = new RetentionRule { KeepLatest = 3, KeepHourly = 0, KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 12, KeepAnnual = 2 },
    };

    [Fact]
    public void Restore_requests_always_restore_full_depth_and_never_overwrite()
    {
        var json = JsonShape.Parse(ServerHarness.Serialize(new RestoreRequestDto("k0123456789abcdef0123456789abcdef", new FilesystemOutput(@"C:\restore"), new RestoreOptions())));
        Assert.Equal(int.MaxValue, JsonShape.Get(json, "options.restoreDirEntryAtDepth").GetInt32());
        Assert.False(JsonShape.Get(json, "options.incremental").GetBoolean());
        foreach (var flag in new[] { "overwriteFiles", "overwriteDirectories", "overwriteSymlinks" })
        {
            Assert.False(JsonShape.Get(json, "fsOutput." + flag).GetBoolean());
        }
    }

    [Fact]
    public void The_policy_covers_every_section_with_the_job_values()
    {
        var job = Job();
        var json = JsonShape.Parse(ServerHarness.Serialize(KopiaPolicyMapper.ToPolicy(job)));

        Assert.Equal(3, JsonShape.Get(json, "retention.keepLatest").GetInt32());
        Assert.Equal(0, JsonShape.Get(json, "retention.keepHourly").GetInt32());
        Assert.Equal(7, JsonShape.Get(json, "retention.keepDaily").GetInt32());
        Assert.Equal(4, JsonShape.Get(json, "retention.keepWeekly").GetInt32());
        Assert.Equal(12, JsonShape.Get(json, "retention.keepMonthly").GetInt32());
        Assert.Equal(2, JsonShape.Get(json, "retention.keepAnnual").GetInt32());
        Assert.False(JsonShape.Get(json, "retention.ignoreIdenticalSnapshots").GetBoolean());
        Assert.True(JsonShape.Get(json, "scheduling.manual").GetBoolean());
        Assert.Equal("zstd-better-compression", JsonShape.Get(json, "compression.compressorName").GetString());
        Assert.Contains(".zip", JsonShape.Get(json, "compression.neverCompress").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(VolumeShadowCopyPolicyDto.WhenAvailable, JsonShape.Get(json, "osSnapshots.volumeShadowCopy.enable").GetInt32());
        Assert.False(JsonShape.Get(json, "errorHandling.ignoreFileErrors").GetBoolean());
        Assert.False(JsonShape.Get(json, "errorHandling.ignoreDirectoryErrors").GetBoolean());
        Assert.Equal(job.Excludes, JsonShape.Get(json, "files.ignore").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([KopiaPolicyMapper.IgnoreFileName], JsonShape.Get(json, "files.ignoreDotFiles").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(BackupCompression.Off, "none")]
    [InlineData(BackupCompression.Fastest, "zstd-fastest")]
    [InlineData(BackupCompression.Balanced, "zstd")]
    [InlineData(BackupCompression.Smallest, "zstd-better-compression")]
    public void Compression_maps_to_the_zstd_family(BackupCompression compression, string name) =>
        Assert.Equal(name, KopiaPolicyMapper.CompressorName(compression));

    [Fact]
    public void Differences_ignore_what_Kopia_omits_but_catch_drift()
    {
        var expected = KopiaPolicyMapper.ToPolicy(Job());
        var stored = JsonDocument.Parse(ServerHarness.Serialize(expected)).RootElement.Clone();
        Assert.Empty(KopiaPolicyMapper.Differences(expected, stored));

        // Kopia echoes empty sections we never wrote and omits empty lists.
        var echoed = JsonNodeEdit(stored, o =>
        {
            o["metadataCompression"] = new System.Text.Json.Nodes.JsonObject();
            o["logging"] = new System.Text.Json.Nodes.JsonObject { ["entries"] = new System.Text.Json.Nodes.JsonObject() };
        });
        Assert.Empty(KopiaPolicyMapper.Differences(expected, echoed));

        var drifted = JsonNodeEdit(stored, o => o["retention"]!["keepLatest"] = 1);
        Assert.Contains(KopiaPolicyMapper.Differences(expected, drifted), d => d.Contains("retention.keepLatest", StringComparison.Ordinal));

        var missing = JsonNodeEdit(stored, o => o.Remove("compression"));
        Assert.Contains(KopiaPolicyMapper.Differences(expected, missing), d => d.Contains("compression", StringComparison.Ordinal));

        var extra = JsonNodeEdit(stored, o => o["upload"] = new System.Text.Json.Nodes.JsonObject { ["maxParallelSnapshots"] = 4 });
        Assert.Contains(KopiaPolicyMapper.Differences(expected, extra), d => d.Contains("upload.maxParallelSnapshots", StringComparison.Ordinal));

        Assert.NotEmpty(KopiaPolicyMapper.Differences(expected, null));
    }

    [Fact]
    public void The_launch_line_has_every_required_flag_and_no_secret()
    {
        var layout = KopiaRepositoryLayout.For(@"C:\ProgramData\CashGear\Backup", Guid.Empty);
        var cert = new KopiaServerCertificate(@"C:\d\tls\server.crt", @"C:\d\tls\server.key", new byte[32]);
        var args = KopiaServerProcess.BuildArguments(layout, @"C:\d\html", cert);

        Assert.Equal(["server", "start"], args.Take(2));
        foreach (var flag in new[] { "--ui", "--no-grpc", "--shutdown-on-stdin", "--no-persist-credentials", "--no-use-credential-manager", "--address=127.0.0.1:0" })
        {
            Assert.Contains(flag, args);
        }

        Assert.Contains($"--config-file={layout.ConfigFile}", args);
        Assert.Contains($"--cache-directory={layout.Cache}", args);
        Assert.Contains($"--log-dir={layout.Logs}", args);
        Assert.Contains(@"--html=C:\d\html", args);
        Assert.Contains($"--tls-cert-file={cert.CertPath}", args);
        Assert.Contains($"--tls-key-file={cert.KeyPath}", args);
        Assert.DoesNotContain(args, a => a.Contains("password", StringComparison.OrdinalIgnoreCase) || a.Contains("--no-ui", StringComparison.Ordinal));
    }

    [Fact]
    public void The_child_environment_drops_inherited_KOPIA_variables_and_carries_the_secrets()
    {
        var credentials = KopiaServerCredentials.Generate();
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["KOPIA_PASSWORD"] = "inherited-must-go",
            ["KOPIA_CONFIG_PATH"] = @"C:\elsewhere\repository.config",
            ["kopia_log_dir"] = @"C:\elsewhere",
        };

        KopiaServerProcess.ConfigureEnvironment(env, credentials, repositoryPassword: null);
        Assert.Equal(@"C:\Windows", env["PATH"]);
        Assert.False(env.ContainsKey("KOPIA_PASSWORD"));
        Assert.False(env.ContainsKey("KOPIA_CONFIG_PATH"));
        Assert.False(env.ContainsKey("kopia_log_dir"));
        Assert.Equal(credentials.UiUser, env["KOPIA_SERVER_USERNAME"]);
        Assert.Equal(credentials.UiPassword, env["KOPIA_SERVER_PASSWORD"]);
        Assert.Equal(credentials.ControlUser, env["KOPIA_SERVER_CONTROL_USER"]);
        Assert.Equal(credentials.ControlPassword, env["KOPIA_SERVER_CONTROL_PASSWORD"]);
        Assert.Equal("false", env["KOPIA_CHECK_FOR_UPDATES"]);

        using var password = new SecureSecret("repo-password");
        KopiaServerProcess.ConfigureEnvironment(env, credentials, password);
        Assert.Equal("repo-password", env["KOPIA_PASSWORD"]);
    }

    [Fact]
    public void Secrets_never_appear_in_ToString()
    {
        var credentials = KopiaServerCredentials.Generate();
        Assert.DoesNotContain(credentials.UiPassword, credentials.ToString(), StringComparison.Ordinal);
        var request = ServerHarness.FilesystemRepository(@"C:\repo", "super-secret-pw", create: true);
        Assert.DoesNotContain("super-secret-pw", request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("k0123456789abcdef0123456789abcdef", true)]
    [InlineData("3edc5dad27a2ea15dc8a8f03c7dc482c", true)]
    [InlineData("Ik0123456789abcdef0123456789abcdef", true)]
    [InlineData("kdeadbeef", false)]
    [InlineData("k0123456789abcdef0123456789abcdef/sub", false)]
    [InlineData("K0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData(null, false)]
    public void Object_ids_are_validated(string? id, bool valid) =>
        Assert.Equal(valid, KopiaApiClient.IsValidObjectId(id));

    [Fact]
    public void Engine_options_reject_relative_paths_and_bad_identities()
    {
        var good = new KopiaEngineOptions { KopiaExePath = @"C:\p\kopia.exe", DataDirectory = @"C:\d", ClientHostName = "host-1" };
        Assert.Empty(good.Validate());
        Assert.NotEmpty((good with { KopiaExePath = "kopia.exe" }).Validate());
        Assert.NotEmpty((good with { DataDirectory = "data" }).Validate());
        Assert.NotEmpty((good with { ClientHostName = "my host" }).Validate());
        Assert.NotEmpty((good with { ClientUserName = "a@b" }).Validate());
    }

    [Fact]
    public void The_pin_is_embedded_and_matches_the_repository_pin_file()
    {
        Assert.Equal(Testing.KopiaTestBinary.Pin.Version, KopiaPin.Embedded.Version);
        Assert.Equal(Testing.KopiaTestBinary.Pin.ExeSha256, KopiaPin.Embedded.ExeSha256, ignoreCase: true);
    }

    private static JsonElement JsonNodeEdit(JsonElement element, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())!.AsObject();
        edit(node);
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
}
