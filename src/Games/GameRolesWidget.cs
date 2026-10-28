using NetCord.Gateway;

using THOBOTTO.Overview;

namespace THOBOTTO.Games;

// The games and how many wear each one's role.
public sealed class GameRolesWidget(GameDirectory games, GatewayClient gateway) : IWidgetKind
{
    public string Id => "game-roles";

    public string Name => "Game roles";

    public string Description => "every game with how many members have its role";

    public TimeSpan Refresh => TimeSpan.FromMinutes(5);

    public async Task<WidgetView?> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var list = await games.ListAsync(guildId);
        if (list.Count == 0)
            return null;
        var members = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Users.Values.ToList() : [];
        var lines = list.Select(g => $"{GameDirectory.Label(g)} · <@&{g.RoleId}> · {members.Count(m => m.RoleIds.Contains(g.RoleId))} playing{(g.Players is { } p ? $" · sessions of {GameDirectory.PlayerCount(p)}" : "")}");
        return new(null, new() { Title = "Games", Description = string.Join('\n', lines) + "\n-# Press a game on the picker to get its role.", Color = new(0xEB459E) });
    }
}
