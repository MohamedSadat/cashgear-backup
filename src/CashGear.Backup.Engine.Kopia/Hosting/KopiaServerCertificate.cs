using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CashGear.Backup.Engine.Kopia.Hosting;

/// <summary>
/// Self-signed ECDSA P-256 server certificate made fresh for every server start and handed to Kopia as
/// PEM files. We pin its SHA-256, so nothing else on the machine can impersonate the server. The key file
/// is deleted as soon as Kopia has loaded it.
/// </summary>
internal sealed record KopiaServerCertificate(string CertPath, string KeyPath, byte[] Pin)
{
    public static KopiaServerCertificate Create(string directory)
    {
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));

        var certPath = Path.Combine(directory, "server.crt");
        var keyPath = Path.Combine(directory, "server.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new KopiaServerCertificate(certPath, keyPath, SHA256.HashData(cert.RawData));
    }

    public static bool Matches(X509Certificate? presented, byte[] pin) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.GetRawCertData()), pin);

    public void DeleteKeyFile()
    {
        try
        {
            File.Delete(KeyPath);
        }
        catch (IOException)
        {
            // Overwritten by the next start anyway.
        }
    }
}
