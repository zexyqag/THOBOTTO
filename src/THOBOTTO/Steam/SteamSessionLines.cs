using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Modules;

namespace THOBOTTO.Steam;

// A game session's card says how many here own the game on Steam, and who's in without it.
public sealed class SteamSessionLines(IDbContextFactory<BotDbContext> dbFactory, SteamLibrary library, SteamClient client, ModuleState modules, GatewayClient gateway) : IEventDecorator
{
    public async Task<IReadOnlyList<string>> LinesAsync(Event e)
    {
        if (e.GameId is not { } gameId || !client.Configured || !await modules.IsEnabledAsync(e.GuildId, SteamLibrary.ModuleId)
            || !gateway.Cache.Guilds.TryGetValue(e.GuildId, out var guild))
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Games.FindAsync(gameId) is not { } game)
            return [];
        var members = SteamCommands.Members(guild);
        if (await library.AppIdAsync(game.Name, members) is not { } appId)
            return [];
        var (_, owners) = await library.OwnersAsync(appId, members);
        var going = await db.EventRsvps.Where(r => r.EventId == e.Id && (r.Status == RsvpStatuses.In || r.Status == RsvpStatuses.Waiting)).OrderBy(r => r.At).Select(r => r.UserId).ToListAsync();
        var readable = (await library.LinkedAsync(going)).Where(l => !l.Hidden).Select(l => l.UserId).ToHashSet();
        return [Line(owners.Count, [.. going.Where(id => readable.Contains(id) && owners.All(o => o.UserId != id))])];
    }

    public Task PostedAsync(Event e) => Task.CompletedTask;

    public static string Line(int owners, IReadOnlyList<ulong> inWithout)
        => $"🛒 On Steam: {(owners == 1 ? "1 here owns it" : $"{owners} here own it")}"
            + (inWithout.Count == 0 ? "" : $" · in without it: {string.Join(", ", inWithout.Select(id => $"<@{id}>"))}");
}
