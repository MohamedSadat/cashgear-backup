using CashGear.Backup.Contracts.V1;
using CashGear.Backup.Testing;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// A pinned local test server (SFTPGo for SFTP, versitygw for S3) that the agent reaches only through a
/// <see cref="ChaosProxy"/>, so a test can pull the network between them.
/// </summary>
internal sealed class RemoteDestination : IAsyncDisposable
{
    public const string Sftp = "sftp";
    public const string S3 = "s3";

    private readonly IAsyncDisposable _server;
    private readonly Func<CreateDestinationRequest> _request;

    private RemoteDestination(IAsyncDisposable server, ChaosProxy proxy, Func<CreateDestinationRequest> request, IReadOnlyDictionary<string, string>? environment)
    {
        _server = server;
        Proxy = proxy;
        _request = request;
        StorageEnvironment = environment;
    }

    public ChaosProxy Proxy { get; }

    /// <summary>What kopia needs in its environment to reach the storage (the S3 keys), for <see cref="RepositoryCheck"/>.</summary>
    public IReadOnlyDictionary<string, string>? StorageEnvironment { get; }

    /// <summary>A request to create the destination behind the proxy. Carries the server's credential: never log it.</summary>
    public CreateDestinationRequest Request() => _request();

    public static async Task<RemoteDestination> StartAsync(string kind, string root, CancellationToken ct)
    {
        switch (kind)
        {
            case Sftp:
            {
                var sftp = await SftpTestServer.StartAsync(root, ct);
                var proxy = new ChaosProxy(sftp.Port);
                return new RemoteDestination(sftp, proxy, () => SftpRequest(sftp.Username, sftp.Password, sftp.KnownHosts, proxy), null);
            }

            case S3:
            {
                var s3 = await S3TestServer.StartAsync(root, ct);
                var proxy = new ChaosProxy(s3.Port);
                var bucket = s3.CreateBucket("backups");
                return new RemoteDestination(s3, proxy, () => new CreateDestinationRequest
                {
                    Name = "Bucket", Kind = DestinationKind.S3,
                    S3 = new S3Settings
                    {
                        Endpoint = $"{proxy.Host}:{proxy.Port}", Bucket = bucket, Prefix = "pc1/",
                        AccessKeyId = s3.AccessKeyId, SecretAccessKey = s3.SecretAccessKey, InsecureHttp = true,
                    },
                }, new Dictionary<string, string> { ["AWS_ACCESS_KEY_ID"] = s3.AccessKeyId, ["AWS_SECRET_ACCESS_KEY"] = s3.SecretAccessKey });
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "sftp or s3");
        }
    }

    /// <summary>An SFTP destination at the proxy's address, trusting the server's host keys under that address.</summary>
    public static CreateDestinationRequest SftpRequest(string username, string password, IEnumerable<string> knownHosts, ChaosProxy proxy)
    {
        var settings = new SftpSettings { Host = proxy.Host, Port = proxy.Port, Username = username, Password = password, Path = "/backups/pc1" };

        // Lines are "[127.0.0.1]:<server port> <type> <key>"; the agent connects to the proxy's port instead.
        settings.KnownHosts.AddRange(knownHosts.Select(line => $"[{proxy.Host}]:{proxy.Port} {line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]}"));
        return new CreateDestinationRequest { Name = "NAS over SFTP", Kind = DestinationKind.Sftp, Sftp = settings };
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        await _server.DisposeAsync();
    }
}
