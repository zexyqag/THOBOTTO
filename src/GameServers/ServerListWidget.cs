using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Overview;

namespace THOBOTTO.GameServers;

// The game servers on the board, each with what the last poll found.
public sealed class ServerListWidget(IDbContextFactory<BotDbContext> dbFactory, ServerBoardService board, GameDig gameDig) : IWidgetKind
{
    public string Id => "game-servers";

    public string Name => "Game servers";

    public string Description => "every game server on the board and who's playing";

    public TimeSpan Refresh => TimeSpan.FromMinutes(1);

    public async Task<WidgetView?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Game).ThenBy(s => s.Name).ToListAsync();
        if (servers.Count == 0)
            return null;
        var games = (await gameDig.GetGamesAsync()).ToDictionary(g => g.Id, g => g.Name);
        var lines = servers.Select(s =>
        {
            var status = board.LastStatus(s.Id);
            var name = s.Name ?? status?.Name ?? games.GetValueOrDefault(s.Game, s.Game);
            return status is null
                ? $"⚫ **{name}** · `{s.Address}` · offline"
                : $"🟢 **{name}** · `{s.Address}` · {status.Players}/{status.MaxPlayers}{(status.Map is { } map ? $" on {map}" : "")}{(status.Password ? " 🔒" : "")}";
        });
        return new(null, new() { Title = "Game servers", Description = string.Join('\n', lines), Color = new(0x57F287) });
    }
}
