using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KopiaSpike;

/// <summary>
/// Self-signed server certificate generated in .NET and handed to Kopia as PEM files, so we know the
/// pin without parsing Kopia's output and Kopia serves TLS 1.2+ (its in-memory cert forces TLS 1.3).
/// </summary>
public sealed record SpikeCertificate(string CertPath, string KeyPath, byte[] Pin)
{
    public string PinHex => Convert.ToHexStringLower(Pin);

    public static SpikeCertificate Create(string dir)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(7));

        var certPath = Path.Combine(dir, "server.crt");
        var keyPath = Path.Combine(dir, "server.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new SpikeCertificate(certPath, keyPath, SHA256.HashData(cert.RawData));
    }

    public static bool Matches(X509Certificate? presented, byte[] pin) =>
        presented is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.GetRawCertData()), pin);
}
