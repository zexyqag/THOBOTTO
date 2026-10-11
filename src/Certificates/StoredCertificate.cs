using System.Security.Cryptography.X509Certificates;

namespace THOBOTTO.Certificates;

// A self-signed certificate kept as a PFX file, made once and loaded from there afterwards.
public static class StoredCertificate
{
    public static X509Certificate2? Load(string pfx) => File.Exists(pfx) ? X509CertificateLoader.LoadPkcs12FromFile(pfx, null) : null;

    public static X509Certificate2 Create(string pfx, CertificateRequest request, DateTimeOffset until)
    {
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), until);
        Directory.CreateDirectory(Path.GetDirectoryName(pfx)!);
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pkcs12));
        return X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
    }
}
