using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Panel;

namespace THOBOTTO.Backups;

// Makes a server's backup from what's set up now.
public sealed class BackupMaker(
    SettingsPages pages,
    ModuleState modules,
    AccessControl access,
    PersonalityBook personalities,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time)
{
    public async Task<ServerBackup> ExportAsync(Guild guild)
    {
        var settings = new Dictionary<string, JsonObject>();
        foreach (var page in pages.All)
            settings[page.ModuleId] = JsonSerializer.SerializeToNode(await page.LoadAsync(guild.Id), page.Type, JsonSerializerOptions.Web)!.AsObject();

        var enabled = new List<string>();
        foreach (var module in ModuleRegistry.All)
        {
            if (await modules.IsEnabledAsync(guild.Id, module.Id))
                enabled.Add(module.Id);
        }

        var all = await personalities.ListAsync(guild.Id);
        var wearers = (await personalities.AssignmentsAsync(guild.Id))
            .Where(a => all.Any(p => p.Id == a.Value))
            .Select(a => new BackupWearer(a.Key.ToString(), all.First(p => p.Id == a.Value).Name))
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync();
        var playlists = await db.SavedPlaylists.AsNoTracking().Where(p => p.GuildId == guild.Id).OrderBy(p => p.Key).ToListAsync();
        var servers = await db.GameServers.AsNoTracking().Where(s => s.GuildId == guild.Id).ToListAsync();
        var games = await db.Games.AsNoTracking().Where(g => g.GuildId == guild.Id).OrderBy(g => g.Name).ToListAsync();
        var gameIds = games.Select(g => g.Id).ToList();
        var modes = await db.GameModes.AsNoTracking().Where(m => gameIds.Contains(m.GameId)).ToListAsync();
        var series = await db.EventSeries.AsNoTracking().Where(s => s.GuildId == guild.Id && s.Active).OrderBy(s => s.Title).ToListAsync();
        var hubs = await db.VoiceHubs.AsNoTracking().Where(h => h.GuildId == guild.Id).Select(h => h.ChannelId).ToListAsync();
        var grants = await access.ListAsync(guild.Id);

        // Every channel and role referenced, with its name, for a restore elsewhere.
        var channels = new HashSet<ulong>(hubs);
        var roles = new HashSet<ulong>(grants.Select(g => g.RoleId));
        foreach (var (channelIds, roleIds) in settings.Values.Select(BackupIds.Find))
        {
            channels.UnionWith(channelIds);
            roles.UnionWith(roleIds);
        }
        channels.UnionWith(games.Select(g => g.ChannelId).OfType<ulong>());
        channels.UnionWith(series.Select(s => s.ChannelId));
        roles.UnionWith(games.Select(g => g.RoleId));
        roles.UnionWith(series.Select(s => s.PingRoleId).OfType<ulong>());

        static string? Id(ulong? id) => id?.ToString();
        return new()
        {
            ServerId = guild.Id.ToString(),
            ServerName = guild.Name,
            CreatedAt = time.GetUtcNow(),
            Channels = channels.Where(guild.Channels.ContainsKey).ToDictionary(id => id.ToString(), id => new BackupChannel(guild.Channels[id].Name, KindOf(guild.Channels[id]))),
            Roles = roles.Where(guild.Roles.ContainsKey).ToDictionary(id => id.ToString(), id => guild.Roles[id].Name),
            Modules = enabled,
            Settings = settings,
            FollowDiscord = await access.FollowsDiscordAsync(guild.Id),
            Permissions = grants.Select(g => new BackupGrant(g.RoleId.ToString(), g.Permission)).ToList(),
            Personalities = all.Select(PersonalityFile.From).ToList(),
            Wearers = wearers,
            Playlists = playlists.Select(p => new BackupPlaylist(p.Name, p.CreatorId.ToString(), JsonSerializer.Deserialize<List<Track>>(p.Tracks)!)).ToList(),
            GameServers = servers.Select(s => new BackupGameServer(s.Game, s.Host, s.Port, s.Name, s.OwnerId.ToString())).ToList(),
            Games = games.Select(g => new BackupGame(g.Name, g.Emoji, g.RoleId.ToString(), Id(g.ChannelId), g.Players,
                servers.FirstOrDefault(s => s.Id == g.ServerId)?.Address,
                modes.Where(m => m.GameId == g.Id).OrderBy(m => m.Name).Select(m => new BackupMode(m.Name, m.Players)).ToList())).ToList(),
            RecurringEvents = series.Select(s => new BackupSeries(s.Title, s.Description, s.ChannelId.ToString(), s.CreatorId.ToString(), Id(s.PingRoleId),
                s.Days, s.TimeOfDay, s.Zone, s.OpenDaysAhead, s.VoiceMode, s.WantsDiscordEvent, s.Capacity,
                games.FirstOrDefault(g => g.Id == s.GameId)?.Name, s.Mode, s.Active)).ToList(),
            VoiceHubs = hubs.Select(h => h.ToString()).ToList(),
        };
    }

    public static string KindOf(IGuildChannel channel) => channel switch
    {
        CategoryGuildChannel => "category",
        VoiceGuildChannel or StageGuildChannel => "voice",
        _ => "text",
    };
}
