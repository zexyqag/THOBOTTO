namespace THOBOTTO.GameServers;

public enum RemoteConsole
{
    Rcon,
    Telnet,
}

// How the bot reaches a game's server from afar, and how it tells everyone playing there something. A game not listed
// gets Source RCON for the console and no announcements. (GoldSrc games, CS 1.6 and the like, speak an older RCON over
// UDP, so they aren't here.)
public static class GameControls
{
    private sealed record Control(RemoteConsole Console, Func<string, string> Say);

    private static readonly Control Source = new(RemoteConsole.Rcon, t => $"say {t}");

    // Games on the Source engine (GameDig's ids), and Sandstorm, which took its console from them.
    private static readonly string[] SourceGames =
    [
        "alienswarm", "blackmesa", "counterstrike2", "csgo", "css", "dods", "dystopia", "fof", "garrysmod", "hl2d", "hlds",
        "imic", "insurgency", "insurgencysandstorm", "l4d", "l4d2", "nmrih", "nucleardawn", "synergy", "teamfortress2",
        "theship", "zps",
    ];

    private static readonly Dictionary<string, Control> Known = new Dictionary<string, Control>
    {
        ["minecraft"] = Source,
        ["projectzomboid"] = new(RemoteConsole.Rcon, t => $"servermsg \"{Quoted(t)}\""),
        ["sdtd"] = new(RemoteConsole.Telnet, t => $"say \"{Quoted(t)}\""),
        ["ase"] = new(RemoteConsole.Rcon, t => $"ServerChat {t}"),
        ["asa"] = new(RemoteConsole.Rcon, t => $"ServerChat {t}"),
        // Anything that isn't a command is chat.
        ["factorio"] = new(RemoteConsole.Rcon, t => t.TrimStart('/')),
    }.Concat(SourceGames.Select(g => KeyValuePair.Create(g, Source))).ToDictionary();

    public static RemoteConsole Console(string game) => Known.TryGetValue(game, out var control) ? control.Console : RemoteConsole.Rcon;

    // The command that says it to everyone playing; null where the bot doesn't know how.
    public static string? Say(string game, string text) => Known.TryGetValue(game, out var control) ? control.Say(text) : null;

    private static string Quoted(string text) => text.Replace('"', '\'');
}
