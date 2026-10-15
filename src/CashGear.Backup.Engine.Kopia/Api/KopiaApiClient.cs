using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CashGear.Backup.Engine.Kopia.Hosting;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia.Api;

internal enum KopiaUser
{
    Ui,
    Control,
}

/// <summary>A non-2xx answer from the Kopia server. Kopia sends JSON <c>{"code","error"}</c> as text/plain; 401s are plain text.</summary>
internal sealed class KopiaApiException(int status, string? code, string message, string path)
    : Exception($"Kopia API {path.Split('?')[0]} returned HTTP {status}{(code is null ? "" : " " + code)}: {message}")
{
    public const string InvalidPassword = "INVALID_PASSWORD";
    public const string NotInitialized = "NOT_INITIALIZED";
    public const string AlreadyInitialized = "ALREADY_INITIALIZED";
    public const string AlreadyConnected = "ALREADY_CONNECTED";
    public const string StorageConnection = "STORAGE_CONNECTION";
    public const string NotFound = "NOT_FOUND";

    public int Status { get; } = status;
    public string? Code { get; } = code;
    public string KopiaMessage { get; } = message;
}

/// <summary>
/// REST client for one <c>kopia server</c> process. Two Basic-auth users: the UI user (repository, sources,
/// policies, snapshots, restore, tasks; needs the session cookie and CSRF header) and the control user
/// (<c>/api/v1/control/*</c>). TLS is accepted only from the pinned certificate; no proxy is ever used.
/// Request bodies are never logged (repository requests carry the password).
/// </summary>
internal sealed partial class KopiaApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string CsrfHeader = "X-Kopia-Csrf-Token";

    private readonly HttpClient _ui;
    private readonly HttpClient _control;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _csrfLock = new(1, 1);
    private string? _csrfToken;

    public KopiaApiClient(Uri baseAddress, byte[] certificatePin, KopiaServerCredentials credentials, ILogger logger)
    {
        _ui = CreateHttpClient(baseAddress, certificatePin, credentials.UiUser, credentials.UiPassword, useCookies: true);
        _control = CreateHttpClient(baseAddress, certificatePin, credentials.ControlUser, credentials.ControlPassword, useCookies: false);
        _logger = logger;
    }

    public Uri BaseAddress => _ui.BaseAddress!;

    public static HttpClient CreateHttpClient(Uri baseAddress, byte[] certificatePin, string? user, string? password, bool useCookies)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = useCookies,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, _) => KopiaServerCertificate.Matches(cert, certificatePin) },
        };
        var client = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) };
        if (user is not null && password is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        }

        return client;
    }

    // ---- control user ----

    public Task<RepoStatus> GetControlStatusAsync(CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Control, HttpMethod.Get, "/api/v1/control/status", null, ct);

    public Task ControlShutdownAsync(CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Control, HttpMethod.Post, "/api/v1/control/shutdown", new { }, ct);

    // ---- UI user ----

    /// <summary><c>GET /</c> sets the session cookie and embeds the CSRF token in a meta tag of our stub page.</summary>
    public async Task InitializeCsrfAsync(CancellationToken ct)
    {
        await _csrfLock.WaitAsync(ct);
        try
        {
            await FetchCsrfTokenAsync(ct);
        }
        finally
        {
            _csrfLock.Release();
        }
    }

    public Task<RepoStatus> GetRepoStatusAsync(CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/status", null, ct);

    /// <summary>Note: the response is the status from before connecting (<c>connected:false</c>); poll <see cref="GetRepoStatusAsync"/>.</summary>
    public Task<RepoStatus> CreateRepositoryAsync(RepositoryRequest request, CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/repo/create", request, ct);

    /// <summary>Like create, the response says <c>connected:false</c>; poll <see cref="GetRepoStatusAsync"/>.</summary>
    public Task<RepoStatus> ConnectRepositoryAsync(RepositoryRequest request, CancellationToken ct) =>
        SendAsync<RepoStatus>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/repo/connect", request, ct);

    public Task<AlgorithmsResponse> GetAlgorithmsAsync(CancellationToken ct) =>
        SendAsync<AlgorithmsResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/repo/algorithms", null, ct);

    /// <summary>Test support: throttles the repository's storage. Kopia persists this in the repository config.</summary>
    public Task SetThrottleAsync(ThrottleLimits limits, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Put, "/api/v1/repo/throttle", limits, ct);

    public Task<SourcesResponse> ListSourcesAsync(CancellationToken ct) =>
        SendAsync<SourcesResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/sources", null, ct);

    /// <summary>Registers a source (for this client's user@host) with its initial policy.</summary>
    public Task<JsonElement> CreateSourceAsync(CreateSourceRequest request, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources", request, ct);

    /// <summary>The policy defined at exactly this source (not merged with parents); null when none is defined.</summary>
    public async Task<JsonElement?> GetPolicyAsync(SourceInfo source, CancellationToken ct)
    {
        try
        {
            return await SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/policy?" + source.ToQuery(), null, ct);
        }
        catch (KopiaApiException ex) when (ex.Code == KopiaApiException.NotFound)
        {
            return null;
        }
    }

    /// <summary>Replaces the whole policy at this source: anything not sent reverts to the inherited value.</summary>
    public Task SetPolicyAsync(SourceInfo source, KopiaPolicy policy, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Put, "/api/v1/policy?" + source.ToQuery(), policy, ct);

    /// <summary>The effective policy if <paramref name="request"/>.Updates were the policy at this source.</summary>
    public Task<ResolvePolicyResponse> ResolvePolicyAsync(SourceInfo source, ResolvePolicyRequest request, CancellationToken ct) =>
        SendAsync<ResolvePolicyResponse>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/policy/resolve?" + source.ToQuery(), request, ct);

    /// <summary>Starts a snapshot of the source. Kopia answers with neither a task ID nor a snapshot ID.</summary>
    public Task<UploadResponse> StartUploadAsync(SourceInfo source, CancellationToken ct) =>
        SendAsync<UploadResponse>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources/upload?" + source.ToQuery(), new { }, ct);

    /// <summary>Cancels the source's running upload. Kopia then saves an incomplete snapshot and reports the task as SUCCESS.</summary>
    public Task<UploadResponse> CancelUploadAsync(SourceInfo source, CancellationToken ct) =>
        SendAsync<UploadResponse>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/sources/cancel?" + source.ToQuery(), new { }, ct);

    /// <summary>Every snapshot of the source (<c>all=1</c>: identical roots are not collapsed).</summary>
    public Task<SnapshotsResponse> ListSnapshotsAsync(SourceInfo source, CancellationToken ct) =>
        SendAsync<SnapshotsResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/snapshots?all=1&" + source.ToQuery(), null, ct);

    public Task<TaskListResponse> ListTasksAsync(CancellationToken ct) =>
        SendAsync<TaskListResponse>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks", null, ct);

    public Task<TaskInfo> GetTaskAsync(string id, CancellationToken ct) =>
        SendAsync<TaskInfo>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/tasks/" + Uri.EscapeDataString(id), null, ct);

    public Task CancelTaskAsync(string id, CancellationToken ct) =>
        SendAsync<JsonElement>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/tasks/" + Uri.EscapeDataString(id) + "/cancel", new { }, ct);

    public Task<TaskInfo> StartRestoreAsync(RestoreRequestDto request, CancellationToken ct) =>
        SendAsync<TaskInfo>(KopiaUser.Ui, HttpMethod.Post, "/api/v1/restore", request, ct);

    /// <summary>
    /// Reads a directory object. <paramref name="objectId"/> must come from Kopia and pass
    /// <see cref="IsValidObjectId"/>: a malformed ID makes Kopia's handler panic and drop the connection.
    /// </summary>
    public Task<DirectoryManifest> GetDirectoryAsync(string objectId, CancellationToken ct)
    {
        if (!IsValidObjectId(objectId))
        {
            throw new ArgumentException("Not a Kopia object ID.", nameof(objectId));
        }

        return SendAsync<DirectoryManifest>(KopiaUser.Ui, HttpMethod.Get, "/api/v1/objects/" + objectId, null, ct);
    }

    /// <summary>Kopia object IDs: optional indirection prefixes 'I', an optional content prefix g-z, then hex.</summary>
    public static bool IsValidObjectId(string? id) => id is not null && ObjectIdPattern().IsMatch(id);

    public async Task<T> SendAsync<T>(KopiaUser user, HttpMethod method, string pathAndQuery, object? body, CancellationToken ct)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), Json);
        var csrf = user == KopiaUser.Ui;
        var (status, text, tokenUsed) = await SendCoreAsync(user, method, pathAndQuery, json, csrf, ct);
        if (status == (int)HttpStatusCode.Unauthorized && csrf)
        {
            // The session or its CSRF token expired: fetch a new one once, then give up.
            await RefreshCsrfTokenAsync(tokenUsed, ct);
            (status, text, _) = await SendCoreAsync(user, method, pathAndQuery, json, csrf, ct);
        }

        if (status is < 200 or > 299)
        {
            throw ToException(status, text, pathAndQuery);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)
                ?? throw new KopiaApiException(status, null, "empty response body", pathAndQuery);
        }
        catch (JsonException ex)
        {
            throw new KopiaApiException(status, null, $"unexpected response shape ({ex.Message})", pathAndQuery);
        }
    }

    /// <summary>Sends a request and returns status and body without interpreting them (contract and negative tests).</summary>
    public async Task<(int Status, string Body)> SendRawAsync(KopiaUser user, HttpMethod method, string pathAndQuery, string? jsonBody, bool includeCsrf, CancellationToken ct)
    {
        var (status, body, _) = await SendCoreAsync(user, method, pathAndQuery, jsonBody, includeCsrf, ct);
        return (status, body);
    }

    /// <summary>Test support: makes the next UI call fail CSRF validation, as an expired session would.</summary>
    internal void CorruptCsrfTokenForTest() => _csrfToken = new string('0', 64);

    public void Dispose()
    {
        _ui.Dispose();
        _control.Dispose();
        _csrfLock.Dispose();
    }

    private async Task<(int Status, string Body, string? CsrfToken)> SendCoreAsync(
        KopiaUser user, HttpMethod method, string pathAndQuery, string? jsonBody, bool includeCsrf, CancellationToken ct)
    {
        string? token = null;
        if (includeCsrf)
        {
            token = _csrfToken;
            if (token is null)
            {
                await RefreshCsrfTokenAsync(null, ct);
                token = _csrfToken;
            }
        }

        using var request = new HttpRequestMessage(method, pathAndQuery);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Add(CsrfHeader, token);
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await (user == KopiaUser.Ui ? _ui : _control).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        LogExchange(method.Method, pathAndQuery, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
        return ((int)response.StatusCode, text, token);
    }

    private async Task RefreshCsrfTokenAsync(string? failedToken, CancellationToken ct)
    {
        await _csrfLock.WaitAsync(ct);
        try
        {
            // Another caller may already have replaced the token that failed.
            if (_csrfToken == failedToken)
            {
                await FetchCsrfTokenAsync(ct);
            }
        }
        finally
        {
            _csrfLock.Release();
        }
    }

    private async Task FetchCsrfTokenAsync(CancellationToken ct)
    {
        using var response = await _ui.GetAsync("/", ct);
        var html = await response.Content.ReadAsStringAsync(ct);
        LogExchange("GET", "/", (int)response.StatusCode, 0);
        if (!response.IsSuccessStatusCode)
        {
            throw new KopiaApiException((int)response.StatusCode, null, html.Trim(), "/");
        }

        var match = CsrfMeta().Match(html);
        _csrfToken = match.Success
            ? match.Groups[1].Value
            : throw new KopiaApiException((int)response.StatusCode, null, "no kopia-csrf-token meta tag in GET /", "/");
    }

    private static KopiaApiException ToException(int status, string body, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
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

    private void LogExchange(string method, string pathAndQuery, int status, long elapsedMs) =>
        LogHttp(_logger, method, pathAndQuery.Split('?')[0], status, elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Kopia {Method} {Path} -> {Status} in {ElapsedMs} ms")]
    private static partial void LogHttp(ILogger logger, string method, string path, int status, long elapsedMs);

    [GeneratedRegex("<meta name=\"kopia-csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex CsrfMeta();

    [GeneratedRegex("^I*[g-z]?[0-9a-f]{16,}$")]
    private static partial Regex ObjectIdPattern();
}
