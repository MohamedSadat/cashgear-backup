using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Backup.Agent.Security;

namespace CashGear.Backup.Agent.Updates;

/// <summary>The newest published version, as the update feed announces it.</summary>
internal sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("msiUrl")] string MsiUrl,
    [property: JsonPropertyName("msiSha256")] string MsiSha256,
    [property: JsonPropertyName("msiSize")] long MsiSize,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("notes")] string? Notes)
{
    /// <summary>The manifest when one of <paramref name="publicKeys"/> signed it and it is well formed; null otherwise.</summary>
    public static UpdateManifest? Verify(string envelope, IReadOnlyList<string> publicKeys)
    {
        if (SignedEnvelope.VerifiedPayload(envelope, publicKeys) is not { } payload)
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload);
            return manifest is not null
                && System.Version.TryParse(manifest.Version, out _)
                && manifest.MsiSize > 0
                && manifest.MsiSha256.Length == 64
                && Uri.TryCreate(manifest.MsiUrl, UriKind.Absolute, out _)
                    ? manifest
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Sign(ECDsa privateKey) => SignedEnvelope.Sign(JsonSerializer.SerializeToUtf8Bytes(this), privateKey);

    /// <summary>Whether this is newer than <paramref name="current"/> (an informational version such as 1.2.0+abc).</summary>
    public bool IsNewerThan(string current) =>
        System.Version.TryParse(current.Split('+', '-')[0], out var installed) && System.Version.Parse(Version) > installed;
}
