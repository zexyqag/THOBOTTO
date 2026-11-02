using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Modules;

namespace THOBOTTO.Steam;

// A game session's card says how many here own the game on Steam, and who's in without it; while its game is voted
// on, who's in without each of them.
public sealed class SteamSessionLines(IDbContextFactory<BotDbContext> dbFactory, SteamLibrary library, SteamClient client, ModuleState modules, GatewayClient gateway) : IEventDecorator
{
    public async Task<IReadOnlyList<string>> LinesAsync(Event e)
    {
        var stage = EventStages.Of(e);
        if ((e.GameId is null && stage != EventStage.Game) || !client.Configured || !await modules.IsEnabledAsync(e.GuildId, SteamLibrary.ModuleId)
            || !gateway.Cache.Guilds.TryGetValue(e.GuildId, out var guild))
            return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var members = SteamCommands.Members(guild);
        var going = await db.EventRsvps.Where(r => r.EventId == e.Id && (r.Status == RsvpStatuses.In || r.Status == RsvpStatuses.Waiting)).OrderBy(r => r.At).Select(r => r.UserId).ToListAsync();
        var readable = (await library.LinkedAsync(going)).Where(l => !l.Hidden).Select(l => l.UserId).ToHashSet();
        if (stage == EventStage.Game)
        {
            var without = new List<string>();
            foreach (var option in await db.EventGameOptions.Where(o => o.EventId == e.Id).OrderBy(o => o.Order).ToListAsync())
            {
                if (await library.AppIdAsync(option.Name, members) is not { } id)
                    continue;
                var (_, have) = await library.OwnersAsync(id, members);
                if (going.Where(u => readable.Contains(u) && have.All(o => o.UserId != u)).ToList() is { Count: > 0 } lacking)
                    without.Add($"{option.Name}: {string.Join(", ", lacking.Select(u => $"<@{u}>"))}");
            }
            return without.Count == 0 ? [] : [$"🛒 In without it on Steam: {string.Join(" · ", without)}"];
        }
        if (await db.Games.FindAsync(e.GameId) is not { } game || await library.AppIdAsync(game.Name, members) is not { } appId)
            return [];
        var (_, owners) = await library.OwnersAsync(appId, members);
        return [Line(owners.Count, [.. going.Where(id => readable.Contains(id) && owners.All(o => o.UserId != id))])];
    }

    public Task PostedAsync(Event e) => Task.CompletedTask;

    public static string Line(int owners, IReadOnlyList<ulong> inWithout)
        => $"🛒 On Steam: {(owners == 1 ? "1 here owns it" : $"{owners} here own it")}"
            + (inWithout.Count == 0 ? "" : $" · in without it: {string.Join(", ", inWithout.Select(id => $"<@{id}>"))}");
}
