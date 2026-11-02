using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Events;
using THOBOTTO.Games;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;
using THOBOTTO.Steam;

namespace THOBOTTO.Commands;

// Right-click a member, Apps, Profile: what the bot knows of them here, privately: points and rank, the games they
// play, what they're in for, their Steam.
public sealed class ProfileCommand(
    PointsEngine points,
    GameDirectory games,
    SteamLibrary steam,
    ModuleState modules,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : GuildCommandModule
{
    [UserCommand("Profile", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionMessageProperties> ProfileAsync(User user)
    {
        var member = user as GuildUser;
        var lines = new List<string>();
        if (member?.JoinedAt is { } joined)
            lines.Add($"Here since <t:{joined.ToUnixTimeSeconds()}:D>");

        if (await modules.IsEnabledAsync(GuildId, PointsEngine.ModuleId) && await points.GetAsync(GuildId, user.Id) is { } standing)
        {
            var top = await points.TopAsync(GuildId, 1000);
            var rank = top.Select((s, i) => (s.UserId, i)).FirstOrDefault(x => x.UserId == user.Id).i + 1;
            lines.Add($"💰 {points.Rules(GuildId).Format(standing.Balance)}{(rank > 0 ? $" · #{rank} of {top.Count}" : "")}");
        }

        if (member is not null && await modules.IsEnabledAsync(GuildId, GameDirectory.ModuleId)
            && (await games.ListAsync(GuildId)).Where(g => member.RoleIds.Contains(g.RoleId)).Select(GameDirectory.Label).ToList() is { Count: > 0 } played)
            lines.Add($"🎮 Plays {string.Join(", ", played)}");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var now = time.GetUtcNow();
            var going = db.EventRsvps.Where(r => r.UserId == user.Id && r.Status == RsvpStatuses.In).Select(r => r.EventId);
            var upcoming = await db.Events.AsNoTracking()
                .Where(e => e.GuildId == GuildId && e.State == EventStates.Scheduled && e.StartsAt > now && going.Contains(e.Id))
                .OrderBy(e => e.StartsAt).Take(3).ToListAsync();
            if (upcoming.Count > 0)
                lines.Add("📅 In for " + string.Join(", ", upcoming.Select(e =>
                    $"{(e.MessageId is { } message ? $"[{e.Title}]({Notifier.Link(e.GuildId, e.ChannelId, message)})" : e.Title)} <t:{e.StartsAt!.Value.ToUnixTimeSeconds()}:R>")));
        }

        if (await modules.IsEnabledAsync(GuildId, SteamLibrary.ModuleId) && await steam.FindAsync(user.Id) is { } link)
            lines.Add(link.Hidden ? $"🛒 Steam: {link.Name} (game details private)" : $"🛒 Steam: {link.Name}, {await steam.CountAsync(user.Id)} games");

        return new()
        {
            Embeds = [new EmbedProperties { Title = member?.Nickname ?? user.GlobalName ?? user.Username, Description = lines.Count == 0 ? "Nothing to show yet." : string.Join('\n', lines) }],
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }
}
