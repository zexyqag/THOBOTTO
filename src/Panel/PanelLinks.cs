using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

using NetCord.Rest;

namespace THOBOTTO.Panel;

// One-time login links (from /panel), signed so they outlast a restart of the bot, and the panel's own address: the origin of the Discord application's
// "/signin-discord" redirect, set in the Developer Portal, so nothing else needs configuring and a forged
// request can't change where links point.
public sealed class PanelLinks(RestClient rest, IDataProtectionProvider protection, TimeProvider time)
{
    public const string Callback = "/signin-discord";
    private static readonly TimeSpan Valid = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    public sealed record Login(ulong UserId, string Name, ulong? GuildId, DateTimeOffset Expires);

    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.PanelLinks");
    // The links used already, until they'd have expired anyway.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new();
    private (string? Address, DateTimeOffset At)? _address;

    // The panel's https://host, or null when the application has no such redirect.
    public async Task<string?> AddressAsync()
    {
        if (_address is { } known && time.GetUtcNow() - known.At < Fresh)
            return known.Address;
        var application = await rest.GetCurrentApplicationAsync();
        var address = (application.RedirectUris ?? [])
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(u => u?.AbsolutePath == Callback)?.GetLeftPart(UriPartial.Authority);
        _address = (address, time.GetUtcNow());
        return address;
    }

    public string Issue(ulong userId, string name, ulong? guildId)
    {
        var expires = time.GetUtcNow() + Valid;
        var once = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        return _protector.Protect(JsonSerializer.Serialize(new Issued(once, new(userId, name, guildId, expires))));
    }

    // Once only, and only while fresh.
    public Login? Redeem(string token)
    {
        foreach (var (spent, until) in _used.Where(u => u.Value < time.GetUtcNow()).ToList())
            _used.TryRemove(spent, out _);
        try
        {
            var issued = JsonSerializer.Deserialize<Issued>(_protector.Unprotect(token))!;
            return issued.Login.Expires >= time.GetUtcNow() && _used.TryAdd(issued.Once, issued.Login.Expires) ? issued.Login : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed record Issued(string Once, Login Login);
}
