using System.Security.Cryptography.X509Certificates;

namespace THOBOTTO.Certificates;

// A self-signed certificate kept as a PFX file, made once and loaded from there afterwards. The file holds the
// private key, so it and its folder are the owner's alone.
public static class StoredCertificate
{
    public static X509Certificate2? Load(string pfx) => File.Exists(pfx) ? X509CertificateLoader.LoadPkcs12FromFile(pfx, null) : null;

    public static X509Certificate2 Create(string pfx, CertificateRequest request, DateTimeOffset until)
    {
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), until);
        var directory = Path.GetDirectoryName(pfx)!;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var file = new FileStream(pfx, options))
            file.Write(created.Export(X509ContentType.Pkcs12));
        return X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
    }
}
