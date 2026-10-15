using System.Security.Cryptography;
using System.Text.Json;
using CashGear.Backup.Core;

namespace CashGear.Backup.Engine.Kopia.Hosting;

/// <summary>
/// The kopia.exe this build was tested with: tools/kopia/kopia.pin.json, embedded in the assembly at build
/// time so a replaced exe cannot be paired with a replaced pin file.
/// </summary>
internal sealed record KopiaPin(string Version, string ExeSha256, long ExeSize)
{
    private const string ResourceName = "CashGear.Backup.Engine.Kopia.kopia.pin.json";

    private static readonly Lazy<KopiaPin> s_embedded = new(LoadEmbedded);

    public static KopiaPin Embedded => s_embedded.Value;

    /// <summary>
    /// Opens <paramref name="exePath"/> for reading with sharing that blocks writes and deletes, then checks
    /// size and SHA-256. Keep the returned stream open until the process has started, so the file cannot be
    /// swapped between the check and the launch.
    /// </summary>
    public async Task<FileStream> OpenVerifiedAsync(string exePath, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(exePath))
        {
            throw Rejected($"kopia.exe path '{exePath}' is not absolute; only the bundled, pinned binary may run.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Rejected($"kopia.exe could not be opened at '{exePath}': {ex.Message}", ex);
        }

        try
        {
            if (stream.Length != ExeSize)
            {
                throw Rejected($"kopia.exe at '{exePath}' is {stream.Length} bytes; the pinned Kopia {Version} is {ExeSize} bytes.");
            }

            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(actual, ExeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Rejected($"kopia.exe at '{exePath}' has SHA-256 {actual}; the pinned Kopia {Version} has {ExeSha256}.");
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static BackupEngineException Rejected(string message, Exception? inner = null) =>
        new(BackupErrorKind.EngineBinaryRejected, message, inner);

    private static KopiaPin LoadEmbedded()
    {
        using var stream = typeof(KopiaPin).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var exe = root.GetProperty("exe");
        return new KopiaPin(
            root.GetProperty("version").GetString()!,
            exe.GetProperty("sha256").GetString()!,
            exe.GetProperty("size").GetInt64());
    }
}
