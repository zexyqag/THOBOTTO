using System.Text;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Overview;

namespace THOBOTTO.GameServers;

// The game servers, each with what the last check found: a line each, or a card each with who's on.
public sealed class ServerListWidget(IDbContextFactory<BotDbContext> dbFactory, ServerWatch watch, GameDig gameDig, ServerDetails details) : IWidgetKind
{
    public const string KindId = "game-servers";

    private const int MaxPlayerNames = 30;

    public string Id => KindId;

    public string Name => "Game servers";

    public string Description => "every game server and who's playing";

    public IReadOnlyList<WidgetOption> Options =>
    [
        new("show", "Show", "lines", Choices: [new("lines", "A line each"), new("cards", "A card each")]),
        new(WidgetOptions.Columns, "Cards abreast", "2", "1 to 3, in the columns look when the block has its row to itself"),
    ];

    public TimeSpan Refresh => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.AsNoTracking().Where(s => s.GuildId == guildId).OrderBy(s => s.Game).ThenBy(s => s.Name).ToListAsync();
        if (servers.Count == 0)
            return [];
        var games = (await gameDig.GetGamesAsync()).ToDictionary(g => g.Id, g => g.Name);
        var shown = servers.Select(s => (Server: s, Status: watch.LastStatus(s.Id), Game: games.GetValueOrDefault(s.Game, s.Game))).ToList();
        if (options.GetValueOrDefault("show") == "cards")
            return [.. shown.Select(s => Card(s.Server, s.Status, s.Game))];
        var lines = shown.Select(s => (s.Status is null
            ? $"⚫ **{NameOf(s.Server, s.Status, s.Game)}** · `{s.Server.JoinAt}` · offline"
            : $"🟢 **{NameOf(s.Server, s.Status, s.Game)}** · `{s.Server.JoinAt}` · {s.Status.Players}/{s.Status.MaxPlayers}{(s.Status.Map is { } map ? $" on {Escape(map)}" : "")}{(s.Status.Password ? " 🔒" : "")}")
            + (s.Server.Modpack is { } modpack ? $" · {Modpack(s.Server)}" : "") + (Shown(s.Server) is { } password ? $" · password `{password}`" : ""));
        List<WidgetButton> buttons = [.. shown.Where(s => ByButton(s.Server)).Select(s => new WidgetButton(PasswordButton(s.Server), $"Password: {NameOf(s.Server, s.Status, s.Game)}", "🔑"))];
        return [new("Game servers", string.Join('\n', lines), buttons.Count > 0 ? buttons : null)];
    }

    // The button that gives the password privately (ServerPasswordButtons).
    public static string PasswordButton(GameServer server) => $"serverpassword:{server.Id}";

    private static bool ByButton(GameServer server) => server.Password is not null && server.PasswordFor != PasswordShown.Card;

    private string? Shown(GameServer server) => server.PasswordFor == PasswordShown.Card ? details.PasswordOf(server) : null;

    private static string Modpack(GameServer server)
    {
        var name = Escape($"{server.Modpack}{(server.ModpackVersion is { } version ? $" {version}" : "")}");
        return server.ModpackLink is { } link ? $"[{name}](<{link}>)" : name;
    }

    private WidgetBlock Card(GameServer server, ServerStatus? status, string game)
    {
        var text = new StringBuilder();
        text.AppendLine(status is null ? "⚫ **Offline**" : $"🟢 **Online** · {status.Players}/{status.MaxPlayers} players{(status.Password ? " · 🔒" : "")}");
        text.AppendLine($"Game: {game}");
        if (server.Modpack is not null)
            text.AppendLine($"Modpack: {Modpack(server)}");
        if (status?.Map is { } map)
            text.AppendLine($"Map: {Escape(map)}");
        text.AppendLine($"Address: `{server.JoinAt}`");
        if (Shown(server) is { } password)
            text.AppendLine($"Password: `{password}`");
        if (server.Note is { } note)
            text.AppendLine($"-# {Escape(note)}");
        if (status is { PlayerNames.Count: > 0 })
        {
            var more = status.PlayerNames.Count - MaxPlayerNames;
            text.AppendLine($"On now: {string.Join(", ", status.PlayerNames.Take(MaxPlayerNames).Select(Escape))}{(more > 0 ? $" and {more} more" : "")}");
        }
        return new(NameOf(server, status, game), text.ToString().TrimEnd(), ByButton(server) ? [new(PasswordButton(server), "Password", "🔑")] : null);
    }

    private static string NameOf(GameServer server, ServerStatus? status, string game) => Escape(server.Name ?? status?.Name ?? game);

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }
}
