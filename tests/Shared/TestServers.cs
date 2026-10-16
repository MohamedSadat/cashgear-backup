using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace CashGear.Backup.Testing;

/// <summary>
/// The pinned test-only servers in tools/test-servers (SFTPGo for SFTP, versitygw for S3), checked against
/// test-servers.pin.json before use.
/// </summary>
internal static class TestServerBinaries
{
    public const string FetchCommand = @"powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-servers\fetch-test-servers.ps1";

    private static readonly Lazy<Dictionary<string, string>> s_paths = new(Resolve);

    public static string SftpGo => s_paths.Value["sftpgo"];
    public static string VersityGw => s_paths.Value["versitygw"];

    private static Dictionary<string, string> Resolve()
    {
        var folder = RepoPaths.Combine("tools", "test-servers");
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "test-servers.pin.json")));
        var paths = new Dictionary<string, string>();
        foreach (var server in pin.RootElement.GetProperty("servers").EnumerateArray())
        {
            var name = server.GetProperty("name").GetString()!;
            var exe = Path.Combine(folder, name, server.GetProperty("exe").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException($"The {name} test server is missing. Run: {FetchCommand}");
            }

            using var stream = File.OpenRead(exe);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (hash != server.GetProperty("exeSha256").GetString())
            {
                throw new InvalidOperationException($"{exe} does not match its pin. Run: {FetchCommand} -Force");
            }

            paths[name] = exe;
        }

        return paths;
    }
}

/// <summary>A test server process on 127.0.0.1; killed with its children on dispose.</summary>
internal abstract class TestServerProcess : IAsyncDisposable
{
    private Process? _process;

    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }

        _process?.Dispose();
    }

    protected async Task StartAsync(ProcessStartInfo start, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        _process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} exited with code {_process.ExitCode} before listening.");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, Port, ct);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
        }
    }

    protected int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return Port;
    }

    protected static string Secret(int length) =>
        RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", length);
}

/// <summary>SFTPGo in portable mode: one user with a password, serving <see cref="Root"/>.</summary>
internal sealed class SftpTestServer : TestServerProcess
{
    private SftpTestServer(string root)
    {
        Root = Path.Combine(root, "sftp-root");
        ConfigDirectory = Path.Combine(root, "sftp-config");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConfigDirectory);
    }

    public string Root { get; }
    public string ConfigDirectory { get; }
    public string Username { get; } = "cashgear";
    public string Password { get; } = Secret(20);

    /// <summary>known_hosts lines for every host key the server offers (Kopia needs all of them).</summary>
    public IReadOnlyList<string> KnownHosts =>
        [.. Directory.GetFiles(ConfigDirectory, "id_*.pub").Select(f => File.ReadAllText(f).Trim().Split(' '))
            .Select(parts => $"[{Host}]:{Port} {parts[0]} {parts[1]}")];

    public static async Task<SftpTestServer> StartAsync(string root, CancellationToken ct)
    {
        var server = new SftpTestServer(root);
        try
        {
            var start = new ProcessStartInfo(TestServerBinaries.SftpGo) { WorkingDirectory = server.ConfigDirectory };
            foreach (var argument in new[]
            {
                "portable", "--directory", server.Root, "--username", server.Username, "--password", server.Password,
                "--sftpd-port", server.ReservePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--config-dir", server.ConfigDirectory, "--permissions", "*", "--log-file-path", Path.Combine(server.ConfigDirectory, "sftpgo.log"),
            })
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["SFTPGO_SFTPD__BINDINGS__0__ADDRESS"] = "127.0.0.1";
            await server.StartAsync(start, ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }
}

/// <summary>versitygw with its POSIX backend: a bucket is a folder under the data root.</summary>
internal sealed class S3TestServer : TestServerProcess
{
    private S3TestServer(string root)
    {
        Data = Path.Combine(root, "s3-data");
        Metadata = Path.Combine(root, "s3-meta");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Metadata);
    }

    public string Data { get; }
    public string Metadata { get; }
    public string AccessKeyId { get; } = "cg" + Secret(12);
    public string SecretAccessKey { get; } = Secret(32);
    public string Endpoint => $"{Host}:{Port}";

    public static async Task<S3TestServer> StartAsync(string root, CancellationToken ct)
    {
        var server = new S3TestServer(root);
        try
        {
            var start = new ProcessStartInfo(TestServerBinaries.VersityGw);
            foreach (var argument in new[]
            {
                "--access", server.AccessKeyId, "--secret", server.SecretAccessKey,
                "--port", $"127.0.0.1:{server.ReservePort()}", "posix", "--sidecar", server.Metadata, server.Data,
            })
            {
                start.ArgumentList.Add(argument);
            }

            await server.StartAsync(start, ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public string CreateBucket(string name)
    {
        Directory.CreateDirectory(Path.Combine(Data, name));
        return name;
    }
}
