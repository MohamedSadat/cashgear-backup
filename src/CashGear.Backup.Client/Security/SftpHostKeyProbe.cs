using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CashGear.Backup.Client.Security;

/// <summary>One host key of an SFTP server, with the fingerprint people compare against the server's.</summary>
/// <param name="KeyType">ssh-ed25519, ecdsa-sha2-nistp256, ssh-rsa...</param>
/// <param name="Base64">The public key blob, as in known_hosts.</param>
/// <param name="Fingerprint">SHA256:..., as OpenSSH shows it.</param>
public sealed record SftpHostKey(string KeyType, string Base64, string Fingerprint)
{
    public string KnownHostsLine(string host, int port) => port == 22 ? $"{host} {KeyType} {Base64}" : $"[{host}]:{port} {KeyType} {Base64}";
}

/// <summary>Reads an SFTP server's host keys so the user can confirm them before anything is sent to it.</summary>
public interface ISftpHostKeyProbe
{
    /// <summary>Every host key the server offers; an <see cref="IOException"/> when none could be read.</summary>
    Task<IReadOnlyList<SftpHostKey>> ProbeAsync(string host, int port, CancellationToken ct);
}

/// <summary>
/// One SSH handshake per host key algorithm, each stopped as soon as the server shows its key: nothing is logged in
/// and no credential is sent. The destination stores all keys found, because the backup engine accepts a server
/// only if the key it negotiates is among them.
/// </summary>
public sealed class SftpHostKeyProbe : ISftpHostKeyProbe
{
    private const string ProbeUser = "cashgear-host-key-check";

    private static readonly string[] s_algorithms =
        ["ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa"];

    public async Task<IReadOnlyList<SftpHostKey>> ProbeAsync(string host, int port, CancellationToken ct)
    {
        var found = new Dictionary<string, SftpHostKey>(StringComparer.Ordinal);
        Exception? lastError = null;
        foreach (var algorithm in s_algorithms)
        {
            var info = new ConnectionInfo(host, port, ProbeUser, new NoneAuthenticationMethod(ProbeUser)) { Timeout = TimeSpan.FromSeconds(10) };
            if (!info.HostKeyAlgorithms.ContainsKey(algorithm))
            {
                continue;
            }

            foreach (var other in info.HostKeyAlgorithms.Keys.Where(k => k != algorithm).ToList())
            {
                info.HostKeyAlgorithms.Remove(other);
            }

            SftpHostKey? key = null;
            using var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                key = FromBlob(e.HostKey);
                e.CanTrust = false;
            };
            try
            {
                await client.ConnectAsync(ct);
            }
            catch (Exception ex) when (ex is SshException or SocketException or ProxyException or InvalidOperationException or TimeoutException)
            {
                // Expected: the handshake is refused on purpose once the key is known, or the server lacks this algorithm.
                lastError = ex;
            }

            if (key is not null)
            {
                found.TryAdd(key.Base64, key);
            }
        }

        return found.Count > 0
            ? [.. found.Values]
            : throw new IOException($"No host key could be read from {host}:{port}: {lastError?.Message}", lastError);
    }

    /// <summary>The key blob starts with its type as an SSH string (big-endian length, then ASCII).</summary>
    internal static SftpHostKey FromBlob(byte[] blob)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(blob);
        var type = Encoding.ASCII.GetString(blob, 4, length);
        return new SftpHostKey(type, Convert.ToBase64String(blob), "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
    }
}
