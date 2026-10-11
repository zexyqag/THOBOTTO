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

    [Fact]
    public void A_key_stored_in_the_clear_is_told_from_one_under_the_certificate()
    {
        var clear = new MemoryRing();
        Protector(clear, null).Protect("secret");
        var encrypted = new MemoryRing();
        Protector(encrypted, KeyCertificate.LoadOrCreate(_folder)).Protect("secret");
        var id = Guid.Parse(clear.Elements.Single().Attribute("id")!.Value);
        var revocation = $"""<revocation version="1"><revocationDate>2026-10-11T00:00:00Z</revocationDate><key id="{id}" /><reason>test</reason></revocation>""";

        Assert.Equal([id], KeyRing.PlaintextKeys([.. clear.Xml, .. encrypted.Xml, revocation, null]));
    }

    [Fact]
    public async Task Replacing_a_key_stored_in_the_clear_keeps_the_secrets_and_ends_what_it_protected()
    {
        var ring = new MemoryRing();
        var before = Protector(ring, null).Protect("secret");
        var plaintext = KeyRing.PlaintextKeys(ring.Xml);

        using var services = Services(ring, KeyCertificate.LoadOrCreate(_folder));
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
        var store = new Store(protector, before);
        var done = await KeyRing.ReplaceAsync(services.GetRequiredService<IKeyManager>(), protector, DateTimeOffset.UtcNow, TimeSpan.FromDays(90), plaintext, [store]);

        Assert.Equal(1, done);
        Assert.Equal("secret", protector.Unprotect(store.Value));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(before));
        Assert.Equal(2, ring.Elements.Count(e => e.Name.LocalName == "key"));
        Assert.Equal(plaintext, KeyRing.PlaintextKeys(ring.Xml));
        Assert.Contains(ring.Elements, e => e.Name.LocalName == "revocation");
    }

    [Fact]
    public void A_secret_no_key_reads_isnt_encrypted_again()
    {
        var protector = Protector(new MemoryRing(), null);
        Assert.Equal("secret", protector.Unprotect(KeyRing.Reprotect(protector, protector.Protect("secret"))!));
        Assert.Null(KeyRing.Reprotect(protector, "not a token"));
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

    private sealed class Store(IDataProtector protector, string value) : ISecretStore
    {
        public string Value { get; private set; } = value;

        public Task<int> ReprotectAsync()
        {
            Value = KeyRing.Reprotect(protector, Value)!;
            return Task.FromResult(1);
        }
    }
}
