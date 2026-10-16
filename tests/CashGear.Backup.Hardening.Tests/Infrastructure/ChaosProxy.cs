using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// A TCP proxy on 127.0.0.1 in front of a test server, standing in for the network between the PC and a remote
/// destination. <see cref="Cut"/> pulls the network: every open connection is reset and new ones are reset as soon
/// as they arrive, until <see cref="Restore"/>. It counts the bytes sent towards the server, so a test can strike
/// part way through an upload.
/// </summary>
internal sealed class ChaosProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<long, Link> _links = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;
    private long _nextLink;
    private long _bytesToServer;
    private long _bytesToClient;
    private volatile bool _up = true;

    public ChaosProxy(int targetPort)
    {
        TargetPort = targetPort;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    public string Host => "127.0.0.1";
    public int Port { get; }
    public bool IsUp => _up;
    public long BytesToServer => Interlocked.Read(ref _bytesToServer);
    public long BytesToClient => Interlocked.Read(ref _bytesToClient);
    public int OpenConnections => _links.Count;

    /// <summary>The server's port on 127.0.0.1; a restarted server can listen elsewhere while the PC keeps the proxy's address.</summary>
    public int TargetPort { get; set; }

    /// <summary>The network goes down: open connections are reset, new ones refused (reset on arrival).</summary>
    public void Cut()
    {
        _up = false;
        foreach (var link in _links.Values)
        {
            link.Reset();
        }
    }

    /// <summary>The network is back.</summary>
    public void Restore() => _up = true;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        foreach (var link in _links.Values)
        {
            link.Reset();
        }

        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (!_up)
            {
                ResetSocket(client);
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket client)
    {
        var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        client.NoDelay = true;
        var id = Interlocked.Increment(ref _nextLink);
        var link = new Link(client, server);
        _links[id] = link;
        try
        {
            await server.ConnectAsync(new IPEndPoint(IPAddress.Loopback, TargetPort), _stopping.Token);
            if (!_up)
            {
                link.Reset();
                return;
            }

            await Task.WhenAll(
                PumpAsync(client, server, link, n => Interlocked.Add(ref _bytesToServer, n)),
                PumpAsync(server, client, link, n => Interlocked.Add(ref _bytesToClient, n)));
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            link.Reset();
        }
        finally
        {
            _links.TryRemove(id, out _);
            link.Close();
        }
    }

    /// <summary>
    /// Copies one direction. A normal close (FIN) is passed on as one, so a healthy network behaves like a wire;
    /// an error on either side resets the whole connection, as a real network would.
    /// </summary>
    private async Task PumpAsync(Socket from, Socket to, Link link, Action<long> count)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, _stopping.Token);
                if (!_up)
                {
                    link.Reset();
                    return;
                }

                if (read == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _stopping.Token);
                count(read);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            link.Reset();
        }
    }

    /// <summary>Closes with RST instead of FIN, like a connection the network dropped.</summary>
    private static void ResetSocket(Socket socket)
    {
        try
        {
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private sealed class Link(Socket client, Socket server)
    {
        private int _closed;

        /// <summary>Both ends get a RST: what a dropped network looks like.</summary>
        public void Reset()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                ResetSocket(client);
                ResetSocket(server);
            }
        }

        /// <summary>Both directions ended normally.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                client.Dispose();
                server.Dispose();
            }
        }
    }
}
