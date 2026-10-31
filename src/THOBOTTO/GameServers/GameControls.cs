namespace THOBOTTO.GameServers;

public enum RemoteConsole
{
    Rcon,
    Telnet,
    WebRcon,
    PalworldApi,
}

// What a member gives to be whitelisted: the name they play as, their linked Steam account, or a name and a password
// for an account the server makes.
public enum WhitelistBy
{
    Name,
    Steam,
    Account,
}

// A game's whitelist through its console: who's added by what, the commands, and whether an answer means it took.
public sealed record GameWhitelist(WhitelistBy By, string Asks, Func<string, string?, string> Add, Func<string, string> Remove, Func<string, bool> Took, string? Valid = null);

// How the bot reaches a game's server from afar, and how it tells everyone playing there something. A game not listed
// gets Source RCON for the console and no announcements. (GoldSrc games, CS 1.6 and the like, speak an older RCON over
// UDP, so they aren't here.)
public static class GameControls
{
    // MultiPacket: it answers the Source engine's marker for the end of a long answer.
    private sealed record Control(RemoteConsole Console, Func<string, string> Say, bool MultiPacket = false, GameWhitelist? Whitelist = null);

    private static readonly Control Source = new(RemoteConsole.Rcon, t => $"say {t}", MultiPacket: true);

    // Games on the Source engine (GameDig's ids), and Sandstorm, which took its console from them.
    private static readonly string[] SourceGames =
    [
        "alienswarm", "blackmesa", "counterstrike2", "csgo", "css", "dods", "dystopia", "fof", "garrysmod", "hl2d", "hlds",
        "imic", "insurgency", "insurgencysandstorm", "l4d", "l4d2", "nmrih", "nucleardawn", "synergy", "teamfortress2",
        "theship", "zps",
    ];

    private static readonly Dictionary<string, Control> Known = new Dictionary<string, Control>
    {
        ["minecraft"] = new(RemoteConsole.Rcon, t => $"say {t}", Whitelist: new(
            WhitelistBy.Name, "Your Minecraft name", (name, _) => $"whitelist add {name}", name => $"whitelist remove {name}",
            a => a.StartsWith("Added", StringComparison.OrdinalIgnoreCase) || a.Contains("already whitelisted", StringComparison.OrdinalIgnoreCase),
            Valid: "^[A-Za-z0-9_]{3,16}$")),
        // A whitelisted server's players need an account on it, which adduser makes.
        ["projectzomboid"] = new(RemoteConsole.Rcon, t => $"servermsg \"{Quoted(t)}\"", Whitelist: new(
            WhitelistBy.Account, "The name you'll log in with", (name, password) => $"adduser \"{name}\" \"{password}\"", name => $"removeuserfromwhitelist \"{name}\"",
            a => a.Contains("created", StringComparison.OrdinalIgnoreCase),
            Valid: "^[^\"\\s]{1,32}$")),
        // Players are known by their platform's id, so a linked Steam account whitelists them before they've ever joined.
        ["sdtd"] = new(RemoteConsole.Telnet, t => $"say \"{Quoted(t)}\"", Whitelist: new(
            WhitelistBy.Steam, "Your linked Steam account", (id, _) => $"whitelist add {id}", id => $"whitelist remove {id}",
            a => a.Contains("added", StringComparison.OrdinalIgnoreCase) || a.Contains("already", StringComparison.OrdinalIgnoreCase))),
        ["squad"] = new(RemoteConsole.Rcon, t => $"AdminBroadcast {t}"),
        ["vrising"] = new(RemoteConsole.Rcon, t => $"announce {t}"),
        ["rust"] = new(RemoteConsole.WebRcon, t => $"say {t}"),
        ["palworld"] = new(RemoteConsole.PalworldApi, t => $"announce {t}"),
        ["ase"] = new(RemoteConsole.Rcon, t => $"ServerChat {t}"),
        ["asa"] = new(RemoteConsole.Rcon, t => $"ServerChat {t}"),
        // Anything that isn't a command is chat.
        ["factorio"] = new(RemoteConsole.Rcon, t => t.TrimStart('/')),
    }.Concat(SourceGames.Select(g => KeyValuePair.Create(g, Source))).ToDictionary();

    public static RemoteConsole Console(string game) => Known.TryGetValue(game, out var control) ? control.Console : RemoteConsole.Rcon;

    public static bool MultiPacket(string game) => Known.TryGetValue(game, out var control) && control.MultiPacket;

    public static GameWhitelist? Whitelist(string game) => Known.TryGetValue(game, out var control) ? control.Whitelist : null;

    // The command that says it to everyone playing; null where the bot doesn't know how.
    public static string? Say(string game, string text) => Known.TryGetValue(game, out var control) ? control.Say(text) : null;

    private static string Quoted(string text) => text.Replace('"', '\'');
}
