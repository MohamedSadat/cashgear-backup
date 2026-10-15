using System.Security.Cryptography;
using System.Text.Json;

namespace CashGear.Backup.Testing;

/// <summary>
/// Locates the pinned kopia.exe for tests and checks it against tools/kopia/kopia.pin.json.
/// Never falls back to a kopia on PATH.
/// </summary>
internal static class KopiaTestBinary
{
    public const string EnvironmentVariable = "CASHGEAR_KOPIA_EXE";
    public const string FetchCommand = @"powershell -NoProfile -ExecutionPolicy Bypass -File tools\kopia\fetch-kopia.ps1";

    private static readonly Lazy<KopiaPin> s_pin = new(LoadPin);
    private static readonly Lazy<string> s_path = new(ResolveAndVerify);

    public static KopiaPin Pin => s_pin.Value;

    /// <summary>Full path of a kopia.exe whose SHA-256 matches the pin; throws with the fix otherwise.</summary>
    public static string ExePath => s_path.Value;

    private static KopiaPin LoadPin()
    {
        var pinPath = RepoPaths.Combine("tools", "kopia", "kopia.pin.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(pinPath));
        var root = doc.RootElement;
        return new KopiaPin(
            root.GetProperty("version").GetString()!,
            root.GetProperty("exe").GetProperty("sha256").GetString()!);
    }

    private static string ResolveAndVerify()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var path = string.IsNullOrWhiteSpace(fromEnv)
            ? RepoPaths.Combine("tools", "kopia", "kopia.exe")
            : Path.GetFullPath(fromEnv);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"kopia.exe not found at '{path}'. From the repo root run:{Environment.NewLine}  {FetchCommand}" +
                $"{Environment.NewLine}or set {EnvironmentVariable} to a kopia.exe matching tools/kopia/kopia.pin.json.");
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, Pin.ExeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"kopia.exe at '{path}' has SHA-256 {actual}, but the pin expects {Pin.ExeSha256} (Kopia {Pin.Version}). " +
                $"Reinstall it with:{Environment.NewLine}  {FetchCommand} -Force");
        }

        return path;
    }
}

internal sealed record KopiaPin(string Version, string ExeSha256);
