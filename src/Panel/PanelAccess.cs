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
// An admin can view the panel as a role sees it: a preview only ever narrows what they may do (both they and the
// role must be allowed), and nothing can be changed while it's on.
public sealed class PanelAccess(GatewayClient gateway, RestClient rest, AccessControl access, IHttpContextAccessor http, TimeProvider time)
{
    public const string PreviewCookie = "thobotto.viewas";
    // Who may view as a role: whoever sets the roles' permissions.
    public const string PreviewPermission = BotPermissions.ManagePermissions;

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

    public async ValueTask<bool> CanAsync(Guild guild, GuildUser member, string permission)
        => await access.CanAsync(guild, member, permission)
            && (PreviewOf(guild.Id) is not { } role || await access.RolesCanAsync(guild, role == guild.Id ? [] : [role], permission));

    // The role the panel is being viewed as in that server (the server's id for a member without roles), if any.
    public ulong? PreviewOf(ulong guildId)
        => http.HttpContext?.Request.Cookies[PreviewCookie]?.Split(':') is [var g, var r] && g == guildId.ToString() && ulong.TryParse(r, out var role) ? role : null;

    public bool Previewing => http.HttpContext?.Request.Cookies.ContainsKey(PreviewCookie) == true;

    public AccessControl Control => access;

    private (ulong[] Owners, DateTimeOffset At)? _owners;

    // The bot's owner (or its team): who manages what's bot-wide, like the helper bots.
    public async Task<bool> IsBotOwnerAsync(ulong userId)
    {
        var now = time.GetUtcNow();
        if (_owners is not { } known || now - known.At > TimeSpan.FromMinutes(10))
        {
            var application = await rest.GetCurrentBotApplicationInformationAsync();
            ulong[] owners = application.Team is { } team ? [.. team.Users.Select(u => u.Id), team.OwnerId] : application.Owner is { } owner ? [owner.Id] : [];
            _owners = known = (owners, now);
        }
        return known.Owners.Contains(userId);
    }

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
