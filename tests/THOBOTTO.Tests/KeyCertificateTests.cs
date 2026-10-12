using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;

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
    public void The_file_and_its_folder_are_the_owners_alone()
    {
        var folder = Path.Combine(_folder, "keys");
        KeyCertificate.LoadOrCreate(folder);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "keys.pfx")));
        }
    }

    [Fact]
    public void New_keys_are_encrypted_with_it()
    {
        var ring = new MemoryRing();
        Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Protect("secret");
        var xml = ring.Xml.Single();
        Assert.Contains("encryptedSecret", xml);
        Assert.DoesNotContain("masterKey", xml);
    }

    // Keys made before the certificate are stored in the clear; they stay readable once it's there.
    [Fact]
    public void Keys_made_before_the_certificate_still_read()
    {
        var ring = new MemoryRing();
        var token = Protector(ring, null).Protect("secret");
        Assert.Equal("secret", Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Unprotect(token));
    }

    [Fact]
    public void Keys_encrypted_with_it_need_it()
    {
        var ring = new MemoryRing();
        var token = Protector(ring, KeyCertificate.LoadOrCreate(_folder)).Protect("secret");
        Assert.Throws<CryptographicException>(() => Protector(ring, null).Unprotect(token));
    }

    private static IDataProtector Protector(IXmlRepository ring, X509Certificate2? certificate)
        => Services(ring, certificate).GetRequiredService<IDataProtectionProvider>().CreateProtector("test");

    private static ServiceProvider Services(IXmlRepository ring, X509Certificate2? certificate)
    {
        var services = new ServiceCollection();
        var protection = services.AddDataProtection().SetApplicationName("test");
        if (certificate is not null)
            protection.ProtectKeysWithCertificate(certificate);
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = ring);
        return services.BuildServiceProvider();
    }

    private sealed class MemoryRing : IXmlRepository
    {
        public List<XElement> Elements { get; } = [];

        public IEnumerable<string> Xml => Elements.Select(e => e.ToString(SaveOptions.DisableFormatting));

        public IReadOnlyCollection<XElement> GetAllElements() => Elements.Select(e => new XElement(e)).ToList();

        public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
    }

}
