namespace CashGear.Backup.Agent.Updates;

/// <summary>Where updates come from; network failures are <see cref="HttpRequestException"/>s.</summary>
internal interface IUpdateFeed
{
    /// <summary>The signed manifest (a signed envelope around an <see cref="UpdateManifest"/>).</summary>
    Task<string> GetManifestAsync(CancellationToken ct);

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/>, refusing more than <paramref name="maxBytes"/>.</summary>
    Task DownloadAsync(string url, string path, long maxBytes, CancellationToken ct);
}

/// <summary>The manifest at <see cref="AgentOptions.UpdateManifestUrl"/>; HTTPS only, except a loopback address (tests).</summary>
internal sealed class HttpUpdateFeed(AgentOptions options) : IUpdateFeed, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public async Task<string> GetManifestAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(Checked(options.UpdateManifestUrl), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task DownloadAsync(string url, string path, long maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Checked(url), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new HttpRequestException("The update download is larger than the update feed said.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    public void Dispose() => _http.Dispose();

    internal static Uri Checked(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new HttpRequestException("No update feed is configured for this installation.");
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            ? uri
            : throw new HttpRequestException("Updates are only downloaded from https:// addresses.");
    }
}
