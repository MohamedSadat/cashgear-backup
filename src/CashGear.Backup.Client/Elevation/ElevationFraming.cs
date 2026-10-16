using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;

namespace CashGear.Backup.Client.Elevation;

/// <summary>Length-prefixed protobuf messages on the private pipe between the app and its elevated helper.</summary>
internal static class ElevationFraming
{
    /// <summary>Requests and replies of the Manage calls are small; anything bigger is a broken or hostile peer.</summary>
    public const int MaxMessageSize = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, IMessage message, CancellationToken ct)
    {
        var size = message.CalculateSize();
        var buffer = new byte[4 + size];
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, size);
            message.WriteTo(buffer.AsSpan(4));
            await stream.WriteAsync(buffer, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            // The buffer may hold a repository password.
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>The next message, or null when the other side closed the pipe.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, MessageParser<T> parser, CancellationToken ct) where T : class, IMessage<T>
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new IOException("The pipe closed in the middle of a message.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 0 or > MaxMessageSize)
        {
            throw new IOException($"Refused a {size}-byte message on the elevation pipe.");
        }

        var buffer = new byte[size];
        try
        {
            await stream.ReadExactlyAsync(buffer, ct);
            return parser.ParseFrom(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
