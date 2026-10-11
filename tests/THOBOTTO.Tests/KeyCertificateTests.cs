using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.DataProtection;

using THOBOTTO.Certificates;

namespace THOBOTTO.Tests;

public class KeyCertificateTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("thobotto-keys-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_certificate_is_made_once_and_kept()
    {
        var first = KeyCertificate.LoadOrCreate(_folder);
        var again = KeyCertificate.LoadOrCreate(_folder);
        Assert.Equal(first.Thumbprint, again.Thumbprint);
        Assert.NotNull(again.GetRSAPrivateKey());
        Assert.Equal(["keys.pfx"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void New_keys_are_encrypted_with_it()
    {
        var ring = Path.Combine(_folder, "ring");
        Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Protect("secret");
        var xml = File.ReadAllText(Directory.GetFiles(ring).Single());
        Assert.Contains("encryptedSecret", xml);
        Assert.DoesNotContain("masterKey", xml);
    }

    // Keys made before the certificate are stored in the clear; they stay readable once it's there.
    [Fact]
    public void Keys_made_before_the_certificate_still_read()
    {
        var ring = Path.Combine(_folder, "ring");
        var token = Protector(ring, null).Protect("secret");
        Assert.Equal("secret", Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Unprotect(token));
    }

    [Fact]
    public void Keys_encrypted_with_it_need_it()
    {
        var ring = Path.Combine(_folder, "ring");
        var token = Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Protect("secret");
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => Protector(ring, null).Unprotect(token));
    }

    private static IDataProtector Protector(string ring, X509Certificate2? certificate)
    {
        var folder = new DirectoryInfo(ring);
        var provider = certificate is null ? DataProtectionProvider.Create(folder) : DataProtectionProvider.Create(folder, certificate);
        return provider.CreateProtector("test");
    }
}
