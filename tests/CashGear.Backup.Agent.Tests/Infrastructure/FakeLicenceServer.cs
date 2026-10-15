using System.Security.Cryptography;
using CashGear.Backup.Agent.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CashGear.Backup.Agent.Tests.Infrastructure;

/// <summary>A licence server on 127.0.0.1 that signs tokens with its own throwaway key.</summary>
internal sealed class FakeLicenceServer : IAsyncDisposable
{
    public const string ValidKey = "CGB-TEST-7Q2M-ABCD";

    private WebApplication? _app;

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public string Url { get; private set; } = "";

    public LicenceEdition Edition { get; set; } = LicenceEdition.Pro;
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Revoked { get; set; }

    /// <summary>Signs with another key than <see cref="Key"/> (an impostor server).</summary>
    public ECDsa? SignWith { get; set; }

    public int Activations { get; private set; }
    public int Refreshes { get; private set; }

    public static async Task<FakeLicenceServer> StartAsync()
    {
        var server = new FakeLicenceServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/activate", (ActivateBody body) =>
        {
            server.Activations++;
            return body.Key == ValidKey
                ? Results.Ok(new { token = server.Token(body.Machine, DateTimeOffset.UtcNow) })
                : Results.BadRequest(new { error = "This key does not exist." });
        });
        app.MapPost("/refresh", (RefreshBody body) =>
        {
            server.Refreshes++;
            return server.Revoked
                ? Results.Json(new { error = "This licence was cancelled.", revoked = true }, statusCode: 403)
                : Results.Ok(new { token = server.Token(body.Machine, DateTimeOffset.UtcNow) });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        server._app = app;
        server.Url = app.Urls.First() + "/";
        return server;
    }

    public string Token(string machine, DateTimeOffset issuedAt) =>
        LicenceToken.Sign(new LicencePayload("lic-test-1", Edition, machine, issuedAt, ExpiresAt), SignWith ?? Key);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            _app = null;
        }

        Key.Dispose();
    }

    private sealed record ActivateBody(string Key, string Machine, string Product, string Version);

    private sealed record RefreshBody(string LicenceId, string Machine);
}
