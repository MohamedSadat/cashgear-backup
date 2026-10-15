using Bunit;
using CashGear.Backup.Client.Security;
using CashGear.Backup.Contracts.V1;
using CashGear.Backup.UI.Components.Model;
using CashGear.Backup.UI.Components.Pages;
using CashGear.Backup.UI.Components.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Backup.UI.Components.Tests;

/// <summary>The wizard's "where": a network share, an SFTP server (fingerprints confirmed first) or an S3 bucket.</summary>
public class DestinationTypeTests : UiTest
{
    private const string Password = "correct horse battery";
    private readonly FakeProbe _probe = new();

    public DestinationTypeTests() => Services.AddSingleton<ISftpHostKeyProbe>(_probe);

    [Fact]
    public async Task An_sftp_server_is_used_only_after_its_fingerprints_are_confirmed()
    {
        await ConnectAsync();
        var cut = await WhereAsync();
        cut.Find("#dest-type-sftp").Change(true);
        cut.Find("#destination-name").Change("NAS");
        cut.Find("#sftp-host").Change("nas.local");
        cut.Find("#sftp-port").Change("2222");
        cut.Find("#sftp-username").Change("backup");
        cut.Find("#sftp-password").Change("sftp-secret");
        cut.Find("#sftp-path").Change("/backups/pc1");
        FillRepositoryPassword(cut);

        await NextAsync(cut);
        Assert.Contains("Check the server", cut.Find("#step-errors").TextContent, StringComparison.Ordinal);

        await cut.Find("#sftp-check").ClickAsync(new());
        Assert.Equal(("nas.local", 2222), _probe.Asked.Single());
        Assert.Contains("SHA256:ed-fingerprint", cut.Find(".host-keys").TextContent, StringComparison.Ordinal);
        await NextAsync(cut);
        Assert.Contains("Confirm", cut.Find("#step-errors").TextContent, StringComparison.Ordinal);

        cut.Find("#sftp-trust").Change(true);
        await FinishAsync(cut);

        var request = Assert.Single(Agent.CreatedDestinations);
        Assert.Equal(DestinationKind.Sftp, request.Kind);
        Assert.Equal(("nas.local", 2222, "backup", "sftp-secret", "/backups/pc1"), (request.Sftp.Host, request.Sftp.Port, request.Sftp.Username, request.Sftp.Password, request.Sftp.Path));
        Assert.Equal(["[nas.local]:2222 ssh-ed25519 AAAAed", "[nas.local]:2222 ssh-rsa AAAArsa"], request.Sftp.KnownHosts);
        Assert.Equal("", request.Sftp.PrivateKey);
    }

    [Fact]
    public async Task Changing_the_server_after_checking_it_needs_a_new_check()
    {
        await ConnectAsync();
        var cut = await WhereAsync();
        cut.Find("#dest-type-sftp").Change(true);
        cut.Find("#sftp-host").Change("nas.local");
        await cut.Find("#sftp-check").ClickAsync(new());
        cut.Find("#sftp-trust").Change(true);

        cut.Find("#sftp-host").Change("other.local");

        Assert.Empty(cut.FindAll(".host-keys"));
        Assert.Empty(cut.FindAll("#sftp-trust"));
    }

    [Fact]
    public async Task A_private_key_can_replace_the_sftp_password()
    {
        await ConnectAsync();
        var cut = await WhereAsync();
        cut.Find("#dest-type-sftp").Change(true);
        cut.Find("#destination-name").Change("NAS");
        cut.Find("#sftp-host").Change("nas.local");
        cut.Find("#sftp-username").Change("backup");
        cut.Find("#sftp-auth-key").Change(true);
        cut.Find("#sftp-key").Change("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----");
        cut.Find("#sftp-path").Change("/b");
        FillRepositoryPassword(cut);
        await cut.Find("#sftp-check").ClickAsync(new());
        cut.Find("#sftp-trust").Change(true);

        await FinishAsync(cut);

        var sftp = Assert.Single(Agent.CreatedDestinations).Sftp;
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", sftp.PrivateKey, StringComparison.Ordinal);
        Assert.Equal("", sftp.Password);
        Assert.Equal(["nas.local ssh-ed25519 AAAAed", "nas.local ssh-rsa AAAArsa"], sftp.KnownHosts);
    }

    [Fact]
    public async Task An_s3_bucket_uses_https_unless_told_otherwise()
    {
        await ConnectAsync();
        var cut = await WhereAsync();
        cut.Find("#dest-type-s3").Change(true);
        cut.Find("#destination-name").Change("Cloud");
        cut.Find("#s3-endpoint").Change("s3.eu-central-1.amazonaws.com");
        cut.Find("#s3-region").Change("eu-central-1");
        cut.Find("#s3-bucket").Change("pat-backups");
        cut.Find("#s3-prefix").Change("laptop/");
        cut.Find("#s3-access-key").Change("AKIAEXAMPLE");
        cut.Find("#s3-secret").Change("s3-secret-key");
        FillRepositoryPassword(cut);

        await FinishAsync(cut);

        var request = Assert.Single(Agent.CreatedDestinations);
        Assert.Equal(DestinationKind.S3, request.Kind);
        Assert.Equal(("s3.eu-central-1.amazonaws.com", "eu-central-1", "pat-backups", "laptop/", "AKIAEXAMPLE", "s3-secret-key", false),
            (request.S3.Endpoint, request.S3.Region, request.S3.Bucket, request.S3.Prefix, request.S3.AccessKeyId, request.S3.SecretAccessKey, request.S3.InsecureHttp));
    }

    [Fact]
    public async Task A_network_share_needs_a_unc_path_and_an_account()
    {
        await ConnectAsync();
        var cut = await WhereAsync();
        cut.Find("#dest-type-share").Change(true);
        cut.Find("#destination-name").Change("NAS");
        cut.Find("#destination-location").Change(@"E:\not-a-share");
        FillRepositoryPassword(cut);

        await NextAsync(cut);
        var errors = cut.Find("#step-errors").TextContent;
        Assert.Contains(@"\\server\share", errors, StringComparison.Ordinal);
        Assert.Contains("account", errors, StringComparison.Ordinal);

        cut.Find("#destination-location").Change(@"\\nas\backups\pc1");
        cut.Find("#share-username").Change(@"NAS\backup");
        cut.Find("#share-password").Change("share-secret");
        FillRepositoryPassword(cut);
        await FinishAsync(cut);

        var request = Assert.Single(Agent.CreatedDestinations);
        Assert.Equal(DestinationKind.NetworkShare, request.Kind);
        Assert.Equal(@"\\nas\backups\pc1", request.Location);
        Assert.Equal((@"NAS\backup", "share-secret"), (request.Share.Username, request.Share.Password));
    }

    [Fact]
    public void Leaving_the_wizard_forgets_every_secret()
    {
        var draft = new JobDraft { Password = "a", SharePassword = "b", SftpPassword = "c", SftpPrivateKey = "d", S3SecretKey = "e" };
        draft.ForgetPasswords();
        Assert.Equal(("", "", "", "", ""), (draft.Password, draft.SharePassword, draft.SftpPassword, draft.SftpPrivateKey, draft.S3SecretKey));
    }

    private async Task<IRenderedComponent<JobEditor>> WhereAsync()
    {
        var cut = Render<JobEditor>();
        cut.WaitForElement("#job-name");
        cut.Find("#job-name").Change("Documents");
        cut.Find("#source").Change(@"C:\Users\pat\Documents");
        await NextAsync(cut);
        cut.Find("#dest-create").Change(true);
        return cut;
    }

    private static void FillRepositoryPassword(IRenderedComponent<JobEditor> cut)
    {
        cut.Find("#password").Change(Password);
        cut.Find("#password-confirmation").Change(Password);
    }

    private static async Task FinishAsync(IRenderedComponent<JobEditor> cut)
    {
        await NextAsync(cut);
        Assert.Empty(cut.FindAll("#step-errors"));
        await NextAsync(cut);
        await NextAsync(cut);
        await cut.Find("#save").ClickAsync(new());
    }

    private static Task NextAsync(IRenderedComponent<JobEditor> cut) => cut.Find("#next").ClickAsync(new());

    private sealed class FakeProbe : ISftpHostKeyProbe
    {
        public List<(string Host, int Port)> Asked { get; } = [];

        public Task<IReadOnlyList<SftpHostKey>> ProbeAsync(string host, int port, CancellationToken ct)
        {
            Asked.Add((host, port));
            return Task.FromResult<IReadOnlyList<SftpHostKey>>(
            [
                new SftpHostKey("ssh-ed25519", "AAAAed", "SHA256:ed-fingerprint"),
                new SftpHostKey("ssh-rsa", "AAAArsa", "SHA256:rsa-fingerprint"),
            ]);
        }
    }
}
