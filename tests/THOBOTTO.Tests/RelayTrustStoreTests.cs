using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using THOBOTTO.Voice.Relay;

namespace THOBOTTO.Tests;

public class RelayTrustStoreTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void The_store_holds_each_certificate_once()
    {
        var relay = SelfSigned("THOBOTTO voice relay");
        var root = SelfSigned("Some Root");
        var bytes = RelayTrustStore.Build([relay, root, root]);
        var loaded = X509CertificateLoader.LoadPkcs12Collection(bytes, RelayTrustStore.Password);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, c => c.Thumbprint == relay.Thumbprint);
    }

    // Java's keystore sees the entries only with the trusted key usage attribute on each: checked with keytool where there is one.
    [Fact]
    public async Task Java_sees_trusted_entries()
    {
        var keytool = Environment.GetEnvironmentVariable("PATH")!.Split(':').Select(d => Path.Combine(d, "keytool")).FirstOrDefault(File.Exists);
        if (keytool is null)
            return;
        var directory = Path.Combine(Path.GetTempPath(), "thobotto-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var relay = SelfSigned("THOBOTTO voice relay");
            await File.WriteAllBytesAsync(Path.Combine(directory, RelayTrustStore.FileName), RelayTrustStore.Build([relay, SelfSigned("Some Root")]));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(keytool,
                ["-list", "-keystore", Path.Combine(directory, RelayTrustStore.FileName), "-storepass", RelayTrustStore.Password])
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var listed = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(2, listed.Split('\n').Count(l => l.Contains("trustedCertEntry")));
            Assert.Contains("thobotto-relay", listed);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
