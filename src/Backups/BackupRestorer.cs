using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.GameServers;
using THOBOTTO.Games;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Music;
using THOBOTTO.Panel;

namespace THOBOTTO.Backups;

// What a restore does (or did): a line per part, and the channels and roles it couldn't place.
public sealed record RestoreReport(IReadOnlyList<string> Lines, IReadOnlyList<string> Missing);

// Puts a backup's setup on a server. Channels and roles are the same ones where they still exist (the same
// server), else ones with the same name (another server); ones with neither are left out and reported.
// Modules, settings and permissions are replaced as they were; personalities, playlists, games, game
// servers, recurring events and voice hubs are added or updated by name, and others are left alone.
// A dry run goes through the same steps without changing anything, for the preview.
public sealed class BackupRestorer(
    SettingsPages pages,
    ModuleState modules,
    AccessControl access,
    PersonalityBook personalities,
    PlaylistBook playlists,
    GameDirectory games,
    ServerBoardService board,
    EventBoard events,
    HelperFleet fleet,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time)
{
    public async Task<RestoreReport> RestoreAsync(Guild guild, ServerBackup backup, ulong actorId, bool dryRun)
    {
        var lines = new List<string>();
        var missing = new SortedSet<string>();
        ulong? Channel(string? id)
        {
            if (id is null || !ulong.TryParse(id, out var channelId))
                return null;
            if (guild.Channels.ContainsKey(channelId))
                return channelId;
            if (backup.Channels.GetValueOrDefault(id) is not { } wanted)
                return null;
            var found = guild.Channels.Values.FirstOrDefault(c => c.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase) && BackupMaker.KindOf(c) == wanted.Kind);
            if (found is null)
                missing.Add($"{(wanted.Kind == "category" ? "category" : "channel")} {(wanted.Kind == "text" ? "#" : "")}{wanted.Name}");
            return found?.Id;
        }
        ulong? Role(string? id)
        {
            if (id is null || !ulong.TryParse(id, out var roleId))
                return null;
            if (guild.Roles.ContainsKey(roleId))
                return roleId;
            if (backup.Roles.GetValueOrDefault(id) is not { } name)
                return null;
            var found = guild.Roles.Values.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                missing.Add($"role @{name}");
            return found?.Id;
        }

        // Modules: on exactly as in the backup.
        var turnOn = new List<string>();
        var turnOff = new List<string>();
        foreach (var module in ModuleRegistry.All)
        {
            var wanted = backup.Modules.Contains(module.Id);
            if (await modules.IsEnabledAsync(guild.Id, module.Id) == wanted)
                continue;
            (wanted ? turnOn : turnOff).Add(module.Id);
            if (!dryRun)
                await modules.SetEnabledAsync(guild.Id, module.Id, wanted, actorId);
        }
        lines.Add(turnOn.Count + turnOff.Count == 0 ? "Modules: as they are."
            : $"Modules: {string.Join("; ", new[] { turnOn.Count > 0 ? $"on: {string.Join(", ", turnOn)}" : null, turnOff.Count > 0 ? $"off: {string.Join(", ", turnOff)}" : null }.OfType<string>())}.");

        // Settings: each module's page saves them, as the panel would.
        var changed = new List<string>();
        foreach (var page in pages.All)
        {
            if (backup.Settings.GetValueOrDefault(page.ModuleId) is not { } saved)
                continue;
            var before = await page.LoadAsync(guild.Id);
            var after = BackupIds.Remap(saved, id => Channel(id.ToString()), id => Role(id.ToString())).Deserialize(page.Type, JsonSerializerOptions.Web)!;
            if (JsonSerializer.Serialize(before, page.Type) == JsonSerializer.Serialize(after, page.Type))
                continue;
            changed.Add(page.Title);
            if (!dryRun)
                await page.SaveAsync(guild.Id, before, after, actorId);
        }
        lines.Add(changed.Count == 0 ? "Settings: as they are." : $"Settings: {string.Join(", ", changed)}.");

        // Permissions: the backup's grants, and no others.
        var grants = backup.Permissions.Select(g => (Role: Role(g.RoleId), g.Permission)).Where(g => g.Role is not null && BotPermissions.Find(g.Permission) is not null)
            .Select(g => (Role: g.Role!.Value, g.Permission)).ToHashSet();
        var current = (await access.ListAsync(guild.Id)).ToHashSet();
        var revoke = current.Except(grants).ToList();
        var grant = grants.Except(current).ToList();
        if (!dryRun)
        {
            foreach (var (role, permission) in revoke)
                await access.SetAsync(guild.Id, role, permission, false, actorId);
            foreach (var (role, permission) in grant)
                await access.SetAsync(guild.Id, role, permission, true, actorId);
            if (await access.FollowsDiscordAsync(guild.Id) != backup.FollowDiscord)
                await access.SetFollowDiscordAsync(guild.Id, backup.FollowDiscord, actorId);
        }
        lines.Add(revoke.Count + grant.Count == 0 ? "Permissions: as they are." : $"Permissions: {grant.Count} granted, {revoke.Count} revoked.");

        // Personalities, then who wears them.
        var existing = (await personalities.ListAsync(guild.Id)).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var (added, updated) = (0, 0);
        foreach (var file in backup.Personalities)
        {
            if (existing.TryGetValue(file.Name, out var same))
            {
                updated++;
                if (!dryRun)
                {
                    var avatar = file.AvatarData;
                    await personalities.ChangeAsync(guild.Id, same.Id, actorId, "restored from a backup", p =>
                    {
                        (p.Color, p.Avatar, p.AvatarType) = (file.ColorValue, avatar?.Bytes, avatar?.Type);
                        p.Phrases = file.Lines.ToDictionary(l => l.Key, l => l.Value.ToList());
                    });
                }
            }
            else
            {
                added++;
                if (!dryRun)
                    existing[file.Name] = await personalities.CreateAsync(guild.Id, file.Name, file, actorId);
            }
        }
        var worn = 0;
        foreach (var wearer in backup.Wearers)
        {
            if (!ulong.TryParse(wearer.HelperId, out var helperId) || fleet.Helpers.FirstOrDefault(h => h.UserId == helperId && h.InGuild(guild.Id)) is null)
                continue;
            worn++;
            if (!dryRun && existing.TryGetValue(wearer.Personality, out var personality))
                await personalities.AssignAsync(guild.Id, helperId, personality.Id, actorId);
        }
        lines.Add($"Personalities: {added} new, {updated} updated{(worn > 0 ? $"; {worn} helpers wear theirs again" : "")}.");

        // Playlists: replaced by name, keeping who made them.
        foreach (var playlist in dryRun ? [] : backup.Playlists)
            await playlists.SaveAsync(guild.Id, playlist.Name, ulong.Parse(playlist.CreatorId), mayReplaceAny: true, playlist.Tracks);
        lines.Add($"Playlists: {backup.Playlists.Count} saved.");

        await using var db = await dbFactory.CreateDbContextAsync();

        // Game servers: ones not on the board yet.
        var addresses = (await db.GameServers.Where(s => s.GuildId == guild.Id).ToListAsync()).Select(s => s.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newServers = backup.GameServers.Where(s => !addresses.Contains(s.Address)).ToList();
        foreach (var server in dryRun ? [] : newServers)
        {
            await board.AddAsync(new GameServer
            {
                GuildId = guild.Id,
                Game = server.Game,
                Host = server.Host,
                Port = server.Port,
                Name = server.Name,
                OwnerId = ulong.Parse(server.OwnerId),
                CreatedAt = time.GetUtcNow(),
            });
        }
        lines.Add($"Game servers: {newServers.Count} added, {backup.GameServers.Count - newServers.Count} already there.");

        // Games: updated by name, or added when their role is here.
        var serverIds = (await db.GameServers.Where(s => s.GuildId == guild.Id).ToListAsync()).ToDictionary(s => s.Address, s => s.Id, StringComparer.OrdinalIgnoreCase);
        var gamesHere = (await db.Games.Where(g => g.GuildId == guild.Id).ToListAsync()).ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var (gamesAdded, gamesUpdated, gamesSkipped) = (0, 0, 0);
        foreach (var saved in backup.Games)
        {
            var channel = Channel(saved.ChannelId);
            long? serverId = saved.Server is { } address && serverIds.TryGetValue(address, out var sid) ? sid : null;
            Game? game;
            if (gamesHere.TryGetValue(saved.Name, out var same))
            {
                gamesUpdated++;
                game = dryRun ? same : await games.UpdateAsync(same.Id, g => (g.Emoji, g.ChannelId, g.Players, g.ServerId) = (saved.Emoji, channel, saved.Players, serverId), actorId);
            }
            else if (Role(saved.RoleId) is { } role)
            {
                gamesAdded++;
                game = dryRun ? null : await games.AddAsync(new Game
                {
                    GuildId = guild.Id,
                    Name = saved.Name,
                    Emoji = saved.Emoji,
                    RoleId = role,
                    ChannelId = channel,
                    Players = saved.Players,
                    ServerId = serverId,
                    CreatedAt = time.GetUtcNow(),
                }, actorId);
            }
            else
            {
                gamesSkipped++;
                continue;
            }
            if (game is not null && !dryRun)
            {
                gamesHere[game.Name] = game;
                foreach (var mode in saved.Modes)
                    await games.SetModeAsync(game, mode.Name, mode.Players, actorId);
            }
        }
        lines.Add($"Games: {gamesAdded} added, {gamesUpdated} updated{(gamesSkipped > 0 ? $", {gamesSkipped} left out (their role isn't here)" : "")}.");

        // Recurring events: ones not running here yet, where their channel is.
        var running = await db.EventSeries.Where(s => s.GuildId == guild.Id && s.Active).Select(s => s.Title).ToListAsync();
        var (seriesAdded, seriesSkipped) = (0, 0);
        foreach (var saved in backup.RecurringEvents.Where(s => s.Active && !running.Contains(s.Title)))
        {
            if (Channel(saved.ChannelId) is not { } channel)
            {
                seriesSkipped++;
                continue;
            }
            seriesAdded++;
            if (dryRun)
                continue;
            await events.CreateSeriesAsync(new EventSeries
            {
                GuildId = guild.Id,
                ChannelId = channel,
                CreatorId = ulong.Parse(saved.CreatorId),
                Title = saved.Title,
                Description = saved.Description,
                PingRoleId = Role(saved.PingRoleId),
                Days = saved.Days,
                TimeOfDay = saved.TimeOfDay,
                Zone = saved.Zone,
                OpenDaysAhead = saved.OpenDaysAhead,
                VoiceMode = saved.VoiceMode,
                WantsDiscordEvent = saved.WantsDiscordEvent,
                Capacity = saved.Capacity,
                GameId = saved.Game is { } name && gamesHere.TryGetValue(name, out var g) ? g.Id : null,
                Mode = saved.Mode,
                Active = true,
                CreatedAt = time.GetUtcNow(),
            });
        }
        lines.Add($"Recurring events: {seriesAdded} started{(seriesSkipped > 0 ? $", {seriesSkipped} left out (their channel isn't here)" : "")}.");

        // Voice hubs: where their channel is.
        var hubs = await db.VoiceHubs.Where(h => h.GuildId == guild.Id).Select(h => h.ChannelId).ToListAsync();
        var newHubs = backup.VoiceHubs.Select(Channel).OfType<ulong>().Where(c => !hubs.Contains(c)).Distinct().ToList();
        if (!dryRun && newHubs.Count > 0)
        {
            db.VoiceHubs.AddRange(newHubs.Select(c => new Voice.VoiceHub { GuildId = guild.Id, ChannelId = c }));
            await db.SaveChangesAsync();
        }
        lines.Add($"Voice hubs: {newHubs.Count} added.");

        return new(lines, missing.ToList());
    }
}
