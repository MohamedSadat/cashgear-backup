using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CashGear.Backup.Hardening.Tests.Infrastructure;

namespace CashGear.Backup.Hardening.Tests;

/// <summary>
/// Quick checks of the fault-injection tools themselves. These run in the normal solution run; the chaos and scale
/// suites are explicit (docs/hardening.md).
/// </summary>
public sealed class InfrastructureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_proxy_forwards_and_a_cut_resets_open_and_new_connections_until_restored()
    {
        using var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                var socket = await echo.AcceptSocketAsync(Ct);
                _ = Task.Run(async () =>
                {
                    var buffer = new byte[1024];
                    try
                    {
                        int read;
                        while ((read = await socket.ReceiveAsync(buffer, Ct)) > 0)
                        {
                            await socket.SendAsync(buffer.AsMemory(0, read), Ct);
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                    }
                }, Ct);
            }
        }, Ct);
        await using var proxy = new ChaosProxy(((IPEndPoint)echo.LocalEndpoint).Port);

        using var open = await ConnectAsync(proxy.Port);
        Assert.Equal("hello", await EchoAsync(open, "hello"));
        Assert.Equal(5, proxy.BytesToServer);

        proxy.Cut();
        await Assert.ThrowsAnyAsync<Exception>(() => EchoAsync(open, "after the cut"));
        using (var refused = await ConnectAsync(proxy.Port))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => EchoAsync(refused, "while down"));
        }

        proxy.Restore();
        using var again = await ConnectAsync(proxy.Port);
        Assert.Equal("back", await EchoAsync(again, "back"));
        echo.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);
    }

    /// <summary>While up, the proxy must behave like a wire: a server closing normally is a normal close for the client, not a reset.</summary>
    [Fact]
    public async Task The_proxy_passes_a_normal_close_on_as_one()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var proxy = new ChaosProxy(((IPEndPoint)listener.LocalEndpoint).Port);
        using var client = await ConnectAsync(proxy.Port);
        using (var accepted = await listener.AcceptSocketAsync(Ct))
        {
            await accepted.SendAsync("bye"u8.ToArray(), Ct);
            accepted.Shutdown(SocketShutdown.Both);
        }

        var buffer = new byte[16];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var read = 0;
        for (int n; (n = await client.ReceiveAsync(buffer.AsMemory(read), timeout.Token)) > 0;)
        {
            read += n;
        }

        Assert.Equal("bye", System.Text.Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public void The_job_lists_child_processes_and_closing_it_kills_them()
    {
        var job = new ProcessJob();
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            job.Assign(process);
            Assert.Contains(process.Id, job.ProcessIds());
            Assert.True(job.PeakCommit().PeakJob > 0);
        }
        finally
        {
            job.Dispose();
        }

        Assert.True(process.WaitForExit(10_000), "closing the job did not kill its process");
    }

    [Fact]
    public void The_parallel_compare_reports_missing_changed_and_extra_items()
    {
        using var ws = Workspace.Create(nameof(The_parallel_compare_reports_missing_changed_and_extra_items));
        var expected = Path.Combine(ws.Root, "expected");
        var actual = Path.Combine(ws.Root, "actual");
        foreach (var root in new[] { expected, actual })
        {
            Directory.CreateDirectory(Path.Combine(root, "same", "empty"));
            File.WriteAllText(Path.Combine(root, "same", "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "changed.txt"), root == expected ? "before" : "after!");
        }

        File.WriteAllText(Path.Combine(expected, "missing.txt"), "m");
        File.WriteAllText(Path.Combine(actual, "extra.txt"), "x");

        Assert.Empty(Scale.ParallelTreeCompare.Differences(expected, expected));
        Assert.Equal(["content differs: changed.txt (6 vs 6 bytes)", "missing: missing.txt", "extra: extra.txt"],
            Scale.ParallelTreeCompare.Differences(expected, actual));
    }

    [Fact]
    public void The_defaults_are_the_small_variants()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k => k.StartsWith("CASHGEAR_SCALE_", StringComparison.Ordinal)
            || k.StartsWith("CASHGEAR_CHAOS_", StringComparison.Ordinal)), "Sizes are configured in this environment.");
        Assert.Equal(20_000, HardeningSettings.ScaleFileCount);
        Assert.Equal(1_000_000_000, HardeningSettings.ScaleBigFileBytes);
        Assert.Equal(1024 * HardeningSettings.MiB, HardeningSettings.ChaosSourceBytes);
        Assert.True(HardeningSettings.ScaleRestore);
    }

    [Fact]
    public void The_agent_was_built_next_to_the_tests()
    {
        Assert.True(File.Exists(AgentProcess.ExecutablePath));
        Assert.Equal("CashGear.Backup.Agent.exe", Path.GetFileName(AgentProcess.ExecutablePath));
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Ct);
        return socket;
    }

    private static async Task<string> EchoAsync(Socket socket, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        await socket.SendAsync(bytes, Ct);
        var buffer = new byte[bytes.Length];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        for (var read = 0; read < buffer.Length;)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), timeout.Token);
            if (n == 0)
            {
                throw new IOException("connection closed");
            }

            read += n;
        }

        return System.Text.Encoding.ASCII.GetString(buffer);
    }
}
