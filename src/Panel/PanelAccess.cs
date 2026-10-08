using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;

namespace THOBOTTO.Panel;

// Who the signed-in person is in each server the bot is in. Membership comes from Discord (the
// bot asks for the member), so the panel needs no access to the person's own server list.
public sealed class PanelAccess(GatewayClient gateway, RestClient rest, AccessControl access, TimeProvider time)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(ulong Guild, ulong User), (GuildUser? Member, DateTimeOffset At)> _members = new();

    public static ulong? UserId(ClaimsPrincipal user)
        => ulong.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    // The servers the person shares with the bot.
    public async Task<IReadOnlyList<Guild>> GuildsAsync(ulong userId)
    {
        var shared = new List<Guild>();
        foreach (var guild in gateway.Cache.Guilds.Values.OrderBy(g => g.Name))
        {
            if (await MemberAsync(guild.Id, userId) is not null)
                shared.Add(guild);
        }
        return shared;
    }

    public async Task<(Guild Guild, GuildUser Member)?> InAsync(ulong guildId, ulong userId)
        => gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && await MemberAsync(guildId, userId) is { } member ? (guild, member) : null;

    public ValueTask<bool> CanAsync(Guild guild, GuildUser member, string permission) => access.CanAsync(guild, member, permission);

    public AccessControl Control => access;

    private async Task<GuildUser?> MemberAsync(ulong guildId, ulong userId)
    {
        var now = time.GetUtcNow();
        if (_members.TryGetValue((guildId, userId), out var known) && now - known.At < Remember)
            return known.Member;

        GuildUser? member;
        try
        {
            member = await rest.GetGuildUserAsync(guildId, userId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            member = null;
        }
        _members[(guildId, userId)] = (member, now);
        return member;
    }
}
