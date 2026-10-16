using System.Security.Cryptography;
using CashGear.Backup.Agent.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CashGear.Backup.Agent.Tests.Infrastructure;

/// <summary>An update feed on 127.0.0.1: GET /manifest and GET /package.msi, signed with its own throwaway key.</summary>
internal sealed class FakeUpdateServer : IAsyncDisposable
{
    private WebApplication? _app;

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public string Url { get; private set; } = "";
    public string ManifestUrl => Url + "manifest";

    /// <summary>What GET /manifest answers.</summary>
    public string Manifest { get; set; } = "";

    public byte[] Package { get; set; } = [];

    public int Downloads { get; private set; }

    public static async Task<FakeUpdateServer> StartAsync()
    {
        var server = new FakeUpdateServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/manifest", () => Results.Text(server.Manifest, "application/json"));
        app.MapGet("/package.msi", () =>
        {
            server.Downloads++;
            return Results.Bytes(server.Package, "application/octet-stream");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        server._app = app;
        server.Url = app.Urls.First() + "/";
        return server;
    }

    /// <summary>Publishes <paramref name="package"/> as <paramref name="version"/>, signed with <paramref name="signWith"/> (default: <see cref="Key"/>).</summary>
    public void Publish(string version, byte[] package, string? sha256 = null, ECDsa? signWith = null)
    {
        Package = package;
        Manifest = new UpdateManifest(version, Url + "package.msi", sha256 ?? Convert.ToHexString(SHA256.HashData(package)), package.Length,
            DateTimeOffset.UtcNow, "Test release").Sign(signWith ?? Key);
    }

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
}

/// <summary>Records what would have been installed.</summary>
internal sealed class RecordingInstaller : IUpdateInstaller
{
    public List<(string MsiPath, string LogPath)> Started { get; } = [];

    public Task StartAsync(string msiPath, string logPath, CancellationToken ct)
    {
        Started.Add((msiPath, logPath));
        return Task.CompletedTask;
    }
}
