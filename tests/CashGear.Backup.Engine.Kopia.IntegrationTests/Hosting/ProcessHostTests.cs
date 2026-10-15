using System.Diagnostics;
using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia.Hosting;
using CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Hosting;

[Trait("Category", "Integration")]
public class ProcessHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Starts_in_a_kill_on_close_job_with_fresh_tls_and_stops_through_the_control_api()
    {
        using var ws = TestWorkspace.Create(nameof(Starts_in_a_kill_on_close_job_with_fresh_tls_and_stops_through_the_control_api));
        using var log = new TestLog();
        await using var host = ServerHarness.CreateHost(ws, log);
        await host.StartAsync(null, Ct);

        var server = host.CurrentProcess!;
        Assert.True(server.Job.Contains(server.Process));
        Assert.Equal(new Uri("https://127.0.0.1").Host, server.BaseAddress.Host);
        Assert.True(File.Exists(Path.Combine(host.Layout.Tls, "server.crt")));
        Assert.False(File.Exists(Path.Combine(host.Layout.Tls, "server.key")), "the private key stays on disk after start");

        var session = await host.GetSessionAsync(Ct);
        Assert.False((await session.Client.GetControlStatusAsync(Ct)).Connected);

        await host.StopAsync(Ct);
        Assert.True(server.HasExited);
        Assert.Equal(0, server.ExitCode);
        Assert.True(log.Contains("stopped by control-api"), "graceful stop did not use the control API");
        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => host.GetSessionAsync(Ct));
        Assert.Equal(BackupErrorKind.EngineUnavailable, ex.Kind);
    }

    [Fact]
    public void Closing_the_job_object_kills_its_processes()
    {
        using var job = new JobObject();
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(psi)!;
        try
        {
            job.Assign(process);
            Assert.True(job.Contains(process));
            job.Dispose();
            Assert.True(process.WaitForExit(10_000), "the process outlived its job object");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public async Task The_health_check_restarts_a_dead_server_with_new_credentials_and_reopens_the_repository()
    {
        using var ws = TestWorkspace.Create(nameof(The_health_check_restarts_a_dead_server_with_new_credentials_and_reopens_the_repository));
        using var log = new TestLog();
        await using var host = ServerHarness.CreateHost(ws, log, configure: o => o with
        {
            HealthCheckInterval = TimeSpan.FromMilliseconds(300),
            RestartBackoffInitial = TimeSpan.FromMilliseconds(200),
        });
        await host.StartAsync(null, Ct);
        var first = await host.GetSessionAsync(Ct);
        await first.Client.CreateRepositoryAsync(ServerHarness.FilesystemRepository(ws.Destination, "restart-password", create: true), Ct);
        Assert.True((await ServerHarness.WaitForConnectedAsync(first.Client, Ct)).Connected);
        using (var password = new SecureSecret("restart-password"))
        {
            host.SetRepositoryPassword(password);
        }

        var firstCredentials = host.CurrentCredentials!;
        var firstPid = host.CurrentProcess!.ProcessId;
        Process.GetProcessById(firstPid).Kill();

        var second = await WaitForGenerationAsync(host, first.Generation);
        Assert.NotEqual(firstPid, host.CurrentProcess!.ProcessId);
        Assert.NotEqual(firstCredentials.UiPassword, host.CurrentCredentials!.UiPassword);
        Assert.True((await second.Client.GetRepoStatusAsync(Ct)).Connected, "the restarted server did not reopen the repository");
        Assert.Equal(KopiaHostState.Running, host.State);
        Assert.True(log.Contains("exited unexpectedly"));
    }

    [Fact]
    public async Task A_kopia_exe_swapped_while_running_is_rejected_at_restart_and_the_host_faults()
    {
        using var ws = TestWorkspace.Create(nameof(A_kopia_exe_swapped_while_running_is_rejected_at_restart_and_the_host_faults));
        using var log = new TestLog();
        var exe = Path.Combine(ws.Root, "bin", "kopia.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(KopiaTestBinary.ExePath, exe);
        await using var host = ServerHarness.CreateHost(ws, log, exePath: exe, configure: o => o with
        {
            HealthCheckInterval = TimeSpan.FromMilliseconds(300),
            RestartBackoffInitial = TimeSpan.FromMilliseconds(200),
        });
        await host.StartAsync(null, Ct);

        // A running image cannot be written but can be renamed: put a tampered copy in its place, then kill it.
        File.Move(exe, exe + ".old");
        File.Copy(exe + ".old", exe);
        await File.AppendAllTextAsync(exe, "tampered", Ct);
        var process = host.CurrentProcess!.Process;
        process.Kill();
        await process.WaitForExitAsync(Ct);

        var stopwatch = Stopwatch.StartNew();
        while (host.State != KopiaHostState.Faulted)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"host state is {host.State}, expected Faulted");
            await Task.Delay(100, Ct);
        }

        var ex = await Assert.ThrowsAsync<BackupEngineException>(() => host.GetSessionAsync(Ct));
        Assert.Equal(BackupErrorKind.EngineUnavailable, ex.Kind);
        Assert.Equal(BackupErrorKind.EngineBinaryRejected, Assert.IsType<BackupEngineException>(ex.InnerException).Kind);
    }

    [Fact]
    public async Task A_relative_or_missing_kopia_path_is_rejected_without_starting_anything()
    {
        using var ws = TestWorkspace.Create(nameof(A_relative_or_missing_kopia_path_is_rejected_without_starting_anything));
        using var log = new TestLog();
        foreach (var path in new[] { "kopia.exe", Path.Combine(ws.Root, "missing", "kopia.exe") })
        {
            await using var host = ServerHarness.CreateHost(ws, log, exePath: path);
            var ex = await Assert.ThrowsAsync<BackupEngineException>(() => host.StartAsync(null, Ct));
            Assert.Equal(BackupErrorKind.EngineBinaryRejected, ex.Kind);
        }
    }

    private static async Task<KopiaServerSession> WaitForGenerationAsync(KopiaProcessHost host, int previous)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var session = await host.GetSessionAsync(Ct);
                if (session.Generation > previous)
                {
                    return session;
                }
            }
            catch (BackupEngineException)
            {
                // Restarting.
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "the server was not restarted");
            await Task.Delay(100, Ct);
        }
    }
}
