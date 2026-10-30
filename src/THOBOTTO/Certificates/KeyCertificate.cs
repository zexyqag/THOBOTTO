using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace THOBOTTO.Certificates;

// The certificate the data protection keys are encrypted with, kept outside the database so a dump alone can't
// read what the keys protect. Never replaced: keys encrypted with it would become unreadable.
public static class KeyCertificate
{
    public static X509Certificate2 LoadOrCreate(string directory)
    {
        var pfx = Path.Combine(directory, "keys.pfx");
        if (StoredCertificate.Load(pfx) is { } existing)
            return existing;

        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=THOBOTTO data protection keys", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        return StoredCertificate.Create(pfx, request, DateTimeOffset.UtcNow.AddYears(100));
    }
}
