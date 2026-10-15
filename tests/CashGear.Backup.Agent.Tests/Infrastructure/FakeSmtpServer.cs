using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CashGear.Backup.Agent.Tests.Infrastructure;

/// <summary>
/// Just enough SMTP on 127.0.0.1 for the agent's mail sender: no TLS, no authentication. Keeps every message's
/// envelope and DATA.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public FakeSmtpServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<ReceivedMail> Messages { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.local ESMTP");
            var from = "";
            var to = new List<string>();
            while (await reader.ReadLineAsync() is { } line)
            {
                var command = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.local");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 fake.local");
                        break;
                    case "MAIL":
                        from = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        to.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                        {
                            data.AppendLine(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine);
                        }

                        Messages.Enqueue(new ReceivedMail(from, [.. to], data.ToString()));
                        to.Clear();
                        await writer.WriteLineAsync("250 OK queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
    }
}

internal sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Data);
