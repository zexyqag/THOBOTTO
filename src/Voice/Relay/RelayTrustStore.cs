using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace THOBOTTO.Voice.Relay;

// Lavalink's trust store (trust.p12 beside the relay's certificate): the machine's root certificates and the
// relay's own, as trusted entries the way Java's keystore wants them (each tagged with Oracle's "trusted key
// usage", or Java sees no entries). Lavalink then needs no keytool of its own, only its Java options:
// -Djavax.net.ssl.trustStore=/relay/trust.p12 -Djavax.net.ssl.trustStorePassword=changeit
public static class RelayTrustStore
{
    public const string FileName = "trust.p12";

    // Java's own default, the way it ships its cacerts; the store holds public certificates only.
    public const string Password = "changeit";

    private const string TrustedKeyUsage = "2.16.840.1.113894.746875.1.1";
    private const string AnyExtendedKeyUsage = "2.5.29.37.0";
    private const string FriendlyName = "1.2.840.113549.1.9.20";

    // The store written whole (a Java process reads it once, at its start), with how many certificates it holds.
    public static int Write(string directory, X509Certificate2 relay)
    {
        var certificates = new List<X509Certificate2> { relay };
        using (var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
        {
            roots.Open(OpenFlags.ReadOnly);
            certificates.AddRange(roots.Certificates.Cast<X509Certificate2>());
        }
        var path = Path.Combine(directory, FileName);
        File.WriteAllBytes(path + ".tmp", Build(certificates));
        File.Move(path + ".tmp", path, overwrite: true);
        return certificates.Count;
    }

    public static byte[] Build(IReadOnlyList<X509Certificate2> certificates)
    {
        var contents = new Pkcs12SafeContents();
        var seen = new HashSet<string>();
        foreach (var certificate in certificates)
        {
            if (!seen.Add(certificate.Thumbprint))
                continue;
            var bag = contents.AddCertificate(certificate);
            bag.Attributes.Add(new Pkcs9AttributeObject(new Oid(FriendlyName), Bmp(seen.Count == 1 ? "thobotto-relay" : certificate.Thumbprint.ToLowerInvariant())));
            bag.Attributes.Add(new Pkcs9AttributeObject(new Oid(TrustedKeyUsage), ObjectIdentifier(AnyExtendedKeyUsage)));
        }
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(contents);
        builder.SealWithMac(Password, HashAlgorithmName.SHA256, 2048);
        return builder.Encode();
    }

    private static byte[] Bmp(string text)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.BMPString, text);
        return writer.Encode();
    }

    private static byte[] ObjectIdentifier(string oid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteObjectIdentifier(oid);
        return writer.Encode();
    }
}

// Writes the trust store at start, whether or not the relay runs: Lavalink's Java reads the file named in its
// options once, so it has to be there before Lavalink starts (the compose has Lavalink wait for the bot's health).
public sealed class RelayTrust(VoiceRelay relay, ILogger<RelayTrust> logger) : IHostedService
{
    public bool Tried { get; private set; }

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(relay.Directory);
            var certificate = RelayCertificate.LoadOrCreate(relay.Directory, relay.Hosts);
            var count = RelayTrustStore.Write(relay.Directory, certificate);
            logger.LogInformation("Lavalink's trust store written to {Path}: {Count} certificates", Path.Combine(relay.Directory, RelayTrustStore.FileName), count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning("Writing Lavalink's trust store failed: {Message}", ex.Message);
        }
        finally
        {
            Tried = true;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
