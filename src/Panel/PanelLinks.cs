using System.Collections.Concurrent;
using System.Security.Cryptography;

using NetCord.Rest;

namespace THOBOTTO.Panel;

// One-time login links (from /panel), and the panel's own address: the origin of the Discord application's
// "/signin-discord" redirect, set in the Developer Portal, so nothing else needs configuring and a forged
// request can't change where links point.
public sealed class PanelLinks(RestClient rest, TimeProvider time)
{
    public const string Callback = "/signin-discord";
    private static readonly TimeSpan Valid = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    public sealed record Login(ulong UserId, string Name, ulong? GuildId, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Login> _links = new();
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
        foreach (var (token, login) in _links.Where(l => l.Value.Expires < time.GetUtcNow()).ToList())
            _links.TryRemove(token, out _);
        var issued = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        _links[issued] = new(userId, name, guildId, time.GetUtcNow() + Valid);
        return issued;
    }

    // Once only, and only while fresh.
    public Login? Redeem(string token)
        => _links.TryRemove(token, out var login) && login.Expires >= time.GetUtcNow() ? login : null;
}
