using CashGear.Backup.Client.Security;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Client.Tests;

[Trait("Category", "Integration")]
public class SftpHostKeyProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_every_host_key_the_server_offers_without_logging_in()
    {
        var root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "probe-" + Guid.NewGuid().ToString("N")[..8]);
        await using var sftp = await SftpTestServer.StartAsync(root, Ct);

        var keys = await new SftpHostKeyProbe().ProbeAsync(sftp.Host, sftp.Port, Ct);

        Assert.Equal(sftp.KnownHosts.Order(), keys.Select(k => k.KnownHostsLine(sftp.Host, sftp.Port)).Order());
        Assert.All(keys, k => Assert.StartsWith("SHA256:", k.Fingerprint, StringComparison.Ordinal));
        Assert.Contains(keys, k => k.KeyType == "ssh-ed25519");
    }

    [Fact]
    public async Task A_closed_port_is_an_io_error()
    {
        await Assert.ThrowsAsync<IOException>(() => new SftpHostKeyProbe().ProbeAsync("127.0.0.1", 1, Ct));
    }

    [Fact]
    public void Known_hosts_lines_use_brackets_only_for_other_ports()
    {
        var key = new SftpHostKey("ssh-ed25519", "AAAA", "SHA256:x");
        Assert.Equal("nas ssh-ed25519 AAAA", key.KnownHostsLine("nas", 22));
        Assert.Equal("[nas]:2222 ssh-ed25519 AAAA", key.KnownHostsLine("nas", 2222));
    }
}
