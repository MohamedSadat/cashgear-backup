using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KopiaSpike;

public enum KopiaUser
{
    Ui,
    Control,
}

public sealed class KopiaApiException(int status, string? code, string message, string path)
    : Exception($"Kopia API {path} returned HTTP {status}{(code is null ? "" : " " + code)}: {message}")
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>
/// REST client for one kopia server. Two Basic-auth users: the UI user (repo, sources, snapshots,
/// restore, tasks; needs the session cookie + CSRF header) and the control user (/api/v1/control/*).
/// TLS is accepted only when the server certificate matches the pin.
/// </summary>
public sealed partial class KopiaApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _ui;
    private readonly HttpClient _control;
    private readonly ExchangeRecorder _recorder;
    private string? _csrfToken;

    public KopiaApiClient(Uri baseAddress, byte[] certPin, SpikeSecrets secrets, ExchangeRecorder recorder)
    {
        _ui = CreateHttpClient(baseAddress, certPin, secrets.UiUser, secrets.UiPassword, useCookies: true);
        _control = CreateHttpClient(baseAddress, certPin, secrets.ControlUser, secrets.ControlPassword, useCookies: false);
        _recorder = recorder;
    }

    public static HttpClient CreateHttpClient(Uri baseAddress, byte[] certPin, string? user, string? password, bool useCookies)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = useCookies,
            CookieContainer = new CookieContainer(),
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => SpikeCertificate.Matches(cert, certPin) },
        };
        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        if (user is not null && password is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }

        return client;
    }

    /// <summary>Polls the control status endpoint until the server answers.</summary>
    public async Task<RepoStatus> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await SendAsync<RepoStatus>(KopiaUser.Control, HttpMethod.Get, "/api/v1/control/status", null, ct);
            }
            catch (HttpRequestException) when (deadline.Elapsed < timeout)
            {
                await Task.Delay(200, ct);
            }
        }
    }

    /// <summary>GET / as the UI user sets the session cookie and embeds the CSRF token in a meta tag.</summary>
    public async Task InitializeCsrfAsync(CancellationToken ct)
    {
        var (status, body) = await SendRawAsync(KopiaUser.Ui, HttpMethod.Get, "/", null, includeCsrf: false, ct);
        if (status != 200)
        {
            throw new KopiaApiException(status, null, body, "/");
        }

        var match = CsrfMeta().Match(body);
        _csrfToken = match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException("GET / returned no kopia-csrf-token meta tag.");
    }

    public Task<RepoStatus> GetRepoStatusAsync(CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, ct);

    public Task<RepoStatus> CreateFilesystemRepositoryAsync(string path, string password, string username, string hostname, CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/repo/create",
            new CreateRepositoryRequest(
                new StorageConnection("filesystem", new() { ["path"] = path }),
                password,
                [],
                new ClientOptions(hostname, username, "CashGear spike")),
            ct);

    public Task<JsonElement> CreateSourceAsync(string path, JsonElement policy, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources", new CreateSourceRequest(path, false, policy), ct);

    public Task<SourcesResponse> ListSourcesAsync(CancellationToken ct) =>
        SendAsync<SourcesResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/sources", null, ct);

    public Task<JsonElement> TriggerUploadAsync(SourceInfo source, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources/upload?" + source.ToQuery(), new { }, ct);

    public Task<SnapshotsResponse> ListSnapshotsAsync(SourceInfo source, CancellationToken ct) =>
        SendAsync<SnapshotsResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/snapshots?all=1&" + source.ToQuery(), null, ct);

    public Task<TaskListResponse> ListTasksAsync(CancellationToken ct) =>
        SendAsync<TaskListResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks", null, ct);

    public Task<TaskInfo> GetTaskAsync(string id, CancellationToken ct) =>
        SendAsync<TaskInfo>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks/" + Uri.EscapeDataString(id), null, ct);

    public Task<TaskInfo> StartRestoreAsync(RestoreRequest request, CancellationToken ct) =>
        SendAsync<TaskInfo>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/restore", request, ct);

    public Task ControlShutdownAsync(CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Control, HttpMethod.Post, "/api/v1/control/shutdown", new { }, ct);

    public async Task<T> SendAsync<T>(KopiaUser user, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), Json);
        var (status, text) = await SendRawAsync(user, method, path, json, includeCsrf: user == KopiaUser.Ui, ct);
        if (status is < 200 or > 299)
        {
            throw ToException(status, text, path);
        }

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new KopiaApiException(status, null, "empty response body", path);
    }

    /// <summary>Sends a request and returns status and body without interpreting them (for negative tests).</summary>
    public async Task<(int Status, string Body)> SendRawAsync(KopiaUser user, HttpMethod method, string path, string? jsonBody, bool includeCsrf, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        if (includeCsrf)
        {
            request.Headers.Add("X-Kopia-Csrf-Token", _csrfToken ?? throw new InvalidOperationException("Call InitializeCsrfAsync first."));
        }

        var client = user == KopiaUser.Ui ? _ui : _control;
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        _recorder.Record(user.ToString().ToLowerInvariant(), method, path, jsonBody, (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(), text, stopwatch.Elapsed);
        return ((int)response.StatusCode, text);
    }

    public void Dispose()
    {
        _ui.Dispose();
        _control.Dispose();
    }

    // Error bodies are JSON {"code","error"} sent as text/plain; 401s are plain text.
    private static KopiaApiException ToException(int status, string body, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
                return new KopiaApiException(status, code, error.GetString() ?? "", path);
            }
        }
        catch (JsonException)
        {
            // Plain-text body.
        }

        return new KopiaApiException(status, null, body.Trim(), path);
    }

    [GeneratedRegex("<meta name=\"kopia-csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex CsrfMeta();
}
