using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace THOBOTTO.Relay;

// The relay's TLS certificate: made once and kept, for the names Lavalink reaches the bot by. Lavalink
// verifies it, so its public part (relay.crt) has to be in Lavalink's trust store.
public static class RelayCertificate
{
    public static X509Certificate2 LoadOrCreate(string directory, IReadOnlyList<string> hosts)
    {
        var pfx = Path.Combine(directory, "relay.pfx");
        if (File.Exists(pfx))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
            var names = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToHashSet();
            if (hosts.All(h => IPAddress.TryParse(h, out _) || names.Contains(h)) && existing.NotAfter > DateTime.UtcNow.AddDays(30))
                return existing;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=THOBOTTO voice relay", key, HashAlgorithmName.SHA256);
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var ip))
                alternative.AddIpAddress(ip);
            else
                alternative.AddDnsName(host);
        }
        request.CertificateExtensions.Add(alternative.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pkcs12));
        File.WriteAllText(Path.Combine(directory, "relay.crt"), created.ExportCertificatePem());
        return X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
    }
}
