using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// The pinned SFTPGo in serve mode with one user whose disk quota is limited: a NAS that runs out of space. Writes
/// past the quota are refused by the server. <see cref="RestartAsync"/> brings it back with another quota (space
/// freed), keeping its host keys and files. The agent reaches it through <see cref="Proxy"/>, whose address stays the
/// same across restarts.
/// </summary>
internal sealed class QuotaSftpServer : IAsyncDisposable
{
    private Process? _process;

    private QuotaSftpServer(string root)
    {
        Home = Path.Combine(root, "sftp-quota-home");
        ConfigDirectory = Path.Combine(root, "sftp-quota-config");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(ConfigDirectory);
        Proxy = new ChaosProxy(0);
    }

    public string Home { get; }
    public string ConfigDirectory { get; }
    public ChaosProxy Proxy { get; }
    public string Username { get; } = "cashgear";
    public string Password { get; } = RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789", 20);

    /// <summary>known_hosts lines for every host key the server offers, under the server's own port.</summary>
    public IReadOnlyList<string> KnownHosts =>
        [.. Directory.GetFiles(ConfigDirectory, "id_*.pub").Select(f => File.ReadAllText(f).Trim().Split(' '))
            .Select(parts => $"[127.0.0.1]:{Proxy.TargetPort} {parts[0]} {parts[1]}")];

    /// <param name="quotaBytes">The user's quota; 0 is unlimited.</param>
    public static async Task<QuotaSftpServer> StartAsync(string root, long quotaBytes, CancellationToken ct)
    {
        var server = new QuotaSftpServer(root);
        try
        {
            await server.LaunchAsync(quotaBytes, ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops the server and starts it again with <paramref name="quotaBytes"/> (0: unlimited). Files and host keys stay.</summary>
    public async Task RestartAsync(long quotaBytes, CancellationToken ct)
    {
        await StopAsync();
        await LaunchAsync(quotaBytes, ct);
    }

    private async Task LaunchAsync(long quotaBytes, CancellationToken ct)
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var install = Path.GetDirectoryName(TestServerBinaries.SftpGo)!;
        await File.WriteAllTextAsync(Path.Combine(ConfigDirectory, "sftpgo.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sftpd"] = new { bindings = new[] { new { port, address = "127.0.0.1" } } },
            ["httpd"] = new { bindings = Array.Empty<object>(), templates_path = Path.Combine(install, "templates"), static_files_path = Path.Combine(install, "static") },
            ["smtp"] = new { templates_path = Path.Combine(install, "templates") },
            ["telemetry"] = new { bind_port = 0 },
            ["data_provider"] = new { driver = "memory", name = "users.json", track_quota = 1 },
        }), ct);

        // The memory provider loads its users from this dump at start; SFTPGo hashes the plain password on load.
        // Space the home already uses counts against the quota, as on a real NAS.
        var used = Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var dump = Path.Combine(ConfigDirectory, "users.json");
        await File.WriteAllTextAsync(dump, JsonSerializer.Serialize(new
        {
            users = new[]
            {
                new
                {
                    id = 1, status = 1, username = Username, password = Password, home_dir = Home,
                    permissions = new Dictionary<string, string[]> { ["/"] = ["*"] },
                    quota_size = quotaBytes, quota_files = 0, used_quota_size = used,
                },
            },
            folders = Array.Empty<object>(),
            admins = Array.Empty<object>(),
            version = 16,
        }), ct);

        var start = new ProcessStartInfo(TestServerBinaries.SftpGo)
        {
            WorkingDirectory = ConfigDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "serve", "--config-dir", ConfigDirectory, "--log-file-path", Path.Combine(ConfigDirectory, "sftpgo.log"), "--log-level", "info",
        })
        {
            start.ArgumentList.Add(argument);
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("SFTPGo did not start.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"SFTPGo exited with code {_process.ExitCode} before listening; see {ConfigDirectory}\\sftpgo.log.");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, ct);
                break;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
        }

        File.Delete(dump);
        Proxy.TargetPort = port;
    }

    /// <summary>Bytes stored in the user's home: what counts against the quota.</summary>
    public long UsedBytes() => Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories).Sum(f =>
    {
        try
        {
            return new FileInfo(f).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    });

    private async Task StopAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }

        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        await StopAsync();
    }
}
