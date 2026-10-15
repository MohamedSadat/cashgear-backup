using System.Security.Cryptography;

namespace CashGear.Backup.Hardening.Tests.Infrastructure;

/// <summary>
/// Test data that neither compresses nor deduplicates, so a backup has to read, encrypt and write every byte.
/// Written in large blocks from a fast generator; content is random per run, never repeated.
/// </summary>
internal static class SourceData
{
    private const int BlockSize = 4 * 1024 * 1024;

    /// <summary>
    /// About <paramref name="totalBytes"/> of random data in <paramref name="folder"/>, as files of
    /// <paramref name="fileBytes"/> (the last one shorter). Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> WriteRandomFiles(string folder, long totalBytes, long fileBytes = 32 * HardeningSettings.MiB)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        for (var (i, left) = (0, totalBytes); left > 0; i++, left -= fileBytes)
        {
            var path = Path.Combine(folder, $"data-{i:0000}.bin");
            WriteRandomFile(path, Math.Min(left, fileBytes));
            files.Add(path);
        }

        return files;
    }

    /// <summary>Writes <paramref name="bytes"/> of random data and returns the file's SHA-256 (hex).</summary>
    public static string WriteRandomFile(string path, long bytes, Action<long>? progress = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buffer = new byte[BlockSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
        {
            stream.SetLength(bytes);
            for (long written = 0; written < bytes;)
            {
                var chunk = (int)Math.Min(buffer.Length, bytes - written);
                Random.Shared.NextBytes(buffer.AsSpan(0, chunk));
                stream.Write(buffer, 0, chunk);
                hash.AppendData(buffer, 0, chunk);
                written += chunk;
                progress?.Invoke(written);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>SHA-256 (hex) of a file, read sequentially in large blocks.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BlockSize, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
