using System.Security.Cryptography;
using System.Text.Json;

namespace CashGear.Backup.Agent.Security;

/// <summary>
/// The signed JSON envelope the licence server and the update feed use: <c>{"payload": base64, "signature":
/// base64}</c>, where the signature is ECDSA P-256 with SHA-256 over the payload bytes, in IEEE P1363 form (r and s,
/// 32 bytes each). Public keys are SubjectPublicKeyInfo, base64; the private keys never leave their servers.
/// </summary>
internal static class SignedEnvelope
{
    /// <summary>The payload when one of <paramref name="publicKeys"/> signed it; null for anything else.</summary>
    public static byte[]? VerifiedPayload(string envelope, IReadOnlyList<string> publicKeys)
    {
        try
        {
            using var document = JsonDocument.Parse(envelope);
            var payload = Convert.FromBase64String(document.RootElement.GetProperty("payload").GetString() ?? "");
            var signature = Convert.FromBase64String(document.RootElement.GetProperty("signature").GetString() ?? "");
            foreach (var key in publicKeys)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                {
                    return payload;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The servers' side, for tests and as their reference.</summary>
    public static string Sign(byte[] payload, ECDsa privateKey) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["payload"] = Convert.ToBase64String(payload),
            ["signature"] = Convert.ToBase64String(privateKey.SignData(payload, HashAlgorithmName.SHA256)),
        });
}
