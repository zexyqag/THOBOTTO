using NetCord.Gateway;

using THOBOTTO.Overview;

namespace THOBOTTO.Games;

// The games, how many wear each one's role, and a button per game that gives or drops it (GameRoleButtons answers).
public sealed class GameRolesWidget(GameDirectory games, GatewayClient gateway) : IWidgetKind
{
    public const string KindId = "game-roles";

    public string Id => KindId;

    public string Name => "Game roles";

    public string Description => "every game with how many have its role, and buttons to get it";

    public IReadOnlyList<WidgetOption> Options =>
    [
        new("show", "Show", "both", Choices: [new("both", "List and buttons"), new("buttons", "Buttons only"), new("list", "List only")]),
    ];

    public TimeSpan Refresh => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        var list = await games.ListAsync(guildId);
        if (list.Count == 0)
            return [];
        var show = options.GetValueOrDefault("show", "both");
        IReadOnlyList<WidgetButton>? buttons = show == "list" ? null : [.. list.Select(g => new WidgetButton($"gamerole:{g.Id}", g.Name, g.Emoji))];
        if (show == "buttons")
            return [new("Which games do you play?", "Press a game to get its role and hear about its sessions; press again to drop it.", buttons)];
        var members = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Users.Values.ToList() : [];
        var lines = list.Select(g => $"{GameDirectory.Label(g)} · <@&{g.RoleId}> · {members.Count(m => m.RoleIds.Contains(g.RoleId))} playing{(g.Players is { } p ? $" · sessions of {GameDirectory.PlayerCount(p)}" : "")}");
        return [new("Games", string.Join('\n', lines) + (buttons is null ? "" : "\n-# Press a game to get its role; press again to drop it."), buttons)];
    }
}
