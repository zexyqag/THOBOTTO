using System.Security.Cryptography;
using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using THOBOTTO.Data;

namespace THOBOTTO.Certificates;

// What keeps secrets encrypted with the data protection keys (integration settings, helper tokens, Last.fm sessions).
public interface ISecretStore
{
    // Encrypts every stored secret again with the default key; returns how many.
    Task<int> ReprotectAsync();
}

// The data protection keys. A key made before the certificate is stored in the clear and would stay the default
// until it expires, so at start such keys are replaced: a new key encrypted with the certificate takes over, the
// stored secrets are encrypted again with it, and the old keys are revoked and removed. Panel sessions and login
// links made under them stop working.
public sealed class KeyRing(
    IDbContextFactory<BotDbContext> dbFactory,
    IKeyManager keys,
    IOptions<KeyManagementOptions> options,
    IDataProtectionProvider protection,
    IEnumerable<ISecretStore> stores,
    TimeProvider time,
    ILogger<KeyRing> logger)
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.KeyRing");

    public async Task ReplacePlaintextKeysAsync()
    {
        List<string?> xml;
        await using (var db = await dbFactory.CreateDbContextAsync())
            xml = await db.DataProtectionKeys.Select(k => k.Xml).ToListAsync();
        var plaintext = PlaintextKeys(xml);
        if (plaintext.Count == 0)
            return;

        var secrets = await ReplaceAsync(keys, _protector, time.GetUtcNow(), options.Value.NewKeyLifetime, plaintext, stores);
        List<string?> rows = [.. plaintext.SelectMany(id => new[] { $"key-{id:D}", $"revocation-{id:D}" })];
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.DataProtectionKeys.Where(k => rows.Contains(k.FriendlyName)).ExecuteDeleteAsync();
        logger.LogInformation("Replaced {Keys} data protection keys stored in the clear and encrypted {Secrets} stored secrets again; panel sessions and login links from before need a new login", plaintext.Count, secrets);
    }

    // A new key under the certificate becomes the default, the stores encrypt their secrets again with it while
    // the old keys still read them, and then those are revoked. Returns how many secrets were done.
    public static async Task<int> ReplaceAsync(IKeyManager keys, IDataProtector protector, DateTimeOffset now, TimeSpan lifetime, IReadOnlyList<Guid> plaintext, IEnumerable<ISecretStore> stores)
    {
        var canary = protector.Protect(new byte[1]);
        keys.CreateNewKey(now, now + lifetime);
        await CaughtUpAsync(protector, canary, revoked: false);
        var secrets = 0;
        foreach (var store in stores)
            secrets += await store.ReprotectAsync();
        foreach (var id in plaintext)
            keys.RevokeKey(id, "stored in the clear");
        await CaughtUpAsync(protector, canary, revoked: true);
        return secrets;
    }

    // The ids of the keys stored in the clear: key elements whose secret isn't encrypted.
    public static List<Guid> PlaintextKeys(IEnumerable<string?> xml)
        => xml.OfType<string>().Select(XElement.Parse)
            .Where(e => e.Name.LocalName == "key" && !e.Descendants().Any(d => d.Name.LocalName == "encryptedSecret"))
            .Select(e => Guid.Parse(e.Attribute("id")!.Value))
            .ToList();

    // A stored secret encrypted again with the default key; null when no key reads it any more.
    public static string? Reprotect(IDataProtector protector, string value)
    {
        try
        {
            return protector.Protect(protector.Unprotect(value));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // Protectors keep the keys they have while a changed ring is read in the background; a canary made under the
    // old default key tells when the new one is in use, and when the old one is revoked.
    private static async Task CaughtUpAsync(IDataProtector protector, byte[] canary, bool revoked)
    {
        var persisted = (IPersistedDataProtector)protector;
        for (var waited = TimeSpan.Zero; waited < Patience; waited += Pause)
        {
            persisted.DangerousUnprotect(canary, ignoreRevocationErrors: true, out var replaced, out var wasRevoked);
            if (revoked ? wasRevoked : replaced)
                return;
            await Task.Delay(Pause);
        }
        throw new InvalidOperationException("The data protection keys didn't change over.");
    }
}
