using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Games;

// Each game's sessions where HomeAsync sends them, those going to the same place in one line.
public sealed class GamePostings(GameDirectory games, GameSessions sessions, SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var rules = await settings.GetAsync<GameRules>(guild.Id, GameDirectory.ModuleId);
        var homes = new List<(Game Game, ulong? Home)>();
        foreach (var game in await games.ListAsync(guild.Id))
            homes.Add((game, await sessions.HomeAsync(game, rules)));
        var setAt = $"/g/{guild.Id}/games";
        var postings = homes.GroupBy(h => h.Home).Select(g => new Posting(GameDirectory.ModuleId, $"Sessions of {string.Join(", ", g.Select(h => h.Game.Name))}",
            g.Key is { } home ? new Landing.In(home) : new Landing.WhereStarted("where each is planned"), setAt)).ToList();
        if (rules.OfferSessions)
            postings.Add(new(GameDirectory.ModuleId, "Offers to plan a session", new Landing.WhereStarted("where the game's role was pinged"), Posting.Settings(guild.Id, GameDirectory.ModuleId)));
        return postings;
    }
}
