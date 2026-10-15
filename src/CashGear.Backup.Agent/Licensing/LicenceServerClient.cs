using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CashGear.Backup.Agent.Licensing;

/// <summary>The licence server refused: a wrong key, or a licence that is no longer valid.</summary>
internal sealed class LicenceRejectedException(string message, bool revoked) : Exception(message)
{
    public bool Revoked { get; } = revoked;
}

/// <summary>The licence server; network failures are <see cref="HttpRequestException"/>s.</summary>
internal interface ILicenceServer
{
    /// <summary>A signed licence token for this PC.</summary>
    Task<string> ActivateAsync(string key, string machine, string version, CancellationToken ct);

    /// <summary>A fresh token for an activated licence (moves the offline grace period forward).</summary>
    Task<string> RefreshAsync(string licenceId, string machine, CancellationToken ct);
}

/// <summary>
/// POST {server}/activate {key, machine, product, version} and POST {server}/refresh {licenceId, machine}; both answer
/// 200 {"token"} or 4xx {"error", "revoked"}. HTTPS only, except a loopback address (tests).
/// </summary>
internal sealed class HttpLicenceServer(AgentOptions options) : ILicenceServer, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Task<string> ActivateAsync(string key, string machine, string version, CancellationToken ct) =>
        PostAsync("activate", new { key, machine, product = "cashgear-backup", version }, ct);

    public Task<string> RefreshAsync(string licenceId, string machine, CancellationToken ct) =>
        PostAsync("refresh", new { licenceId, machine }, ct);

    public void Dispose() => _http.Dispose();

    private async Task<string> PostAsync(string path, object body, CancellationToken ct)
    {
        var baseUri = ServerUri(options.LicenceServerUrl);
        using var response = await _http.PostAsJsonAsync(new Uri(baseUri, path), body, ct);
        if (response.IsSuccessStatusCode)
        {
            var answer = await response.Content.ReadFromJsonAsync<Answer>(ct);
            return answer?.Token ?? throw new HttpRequestException("The licence server answered without a token.");
        }

        if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            Answer? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<Answer>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
            }

            throw new LicenceRejectedException(error?.Error ?? $"The licence server refused ({(int)response.StatusCode}).", error?.Revoked ?? false);
        }

        throw new HttpRequestException($"The licence server answered {(int)response.StatusCode}.");
    }

    /// <summary>The configured server, HTTPS unless it is this PC; null-safe for "not configured".</summary>
    internal static Uri ServerUri(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured.EndsWith('/') ? configured : configured + "/", UriKind.Absolute, out var uri))
        {
            throw new HttpRequestException("No licence server is configured for this installation.");
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? uri
            : throw new HttpRequestException("The licence server must be an https:// address.");
    }

    private sealed record Answer(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("revoked")] bool Revoked);
}
