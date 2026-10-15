using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Backup.Agent.Security;

namespace CashGear.Backup.Agent.Licensing;

internal enum LicenceEdition
{
    Free,
    Pro,
    Business,
}

/// <summary>What a licence token says. Issued, and re-issued on every refresh, by the licence server for one PC.</summary>
internal sealed record LicencePayload(
    [property: JsonPropertyName("licenceId")] string LicenceId,
    [property: JsonPropertyName("edition")] LicenceEdition Edition,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);

/// <summary>A licence token is a <see cref="SignedEnvelope"/> around a <see cref="LicencePayload"/>.</summary>
internal static class LicenceToken
{
    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public static LicencePayload? Verify(string token, IReadOnlyList<string> publicKeys)
    {
        if (SignedEnvelope.VerifiedPayload(token, publicKeys) is not { } payload)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LicencePayload>(payload, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Sign(LicencePayload payload, ECDsa privateKey) =>
        SignedEnvelope.Sign(JsonSerializer.SerializeToUtf8Bytes(payload, s_json), privateKey);
}
