namespace THOBOTTO.GameServers;

public sealed class GameServer
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    // GameDig game id, e.g. "projectzomboid".
    public required string Game { get; set; }

    public required string Host { get; init; }

    // Null means the game's default port.
    public int? Port { get; init; }

    public string? Name { get; set; }

    public ulong OwnerId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // What players connect to, when it isn't the address the bot checks (a domain, another port).
    public string? JoinAddress { get; set; }

    // The modpack it runs, and where to get it.
    public string? Modpack { get; set; }

    public string? ModpackVersion { get; set; }

    public string? ModpackLink { get; set; }

    // A line for players, e.g. "whitelist: ask an admin".
    public string? Note { get; set; }

    // Its password, protected as the integrations' secrets are (ServerPasswords).
    public string? Password { get; set; }

    // Who gets the password: one of PasswordShown; with Role, the members of PasswordRoleId.
    public string PasswordFor { get; set; } = PasswordShown.Anyone;

    public ulong? PasswordRoleId { get; set; }

    // Remote control (RCON): where the bot reaches it, as host:port, inside the bot's own network only when the bot's
    // owner set it (RconInside); the password protected as the join password is.
    public string? RconAddress { get; set; }

    public string? RconPassword { get; set; }

    public bool RconInside { get; set; }

    // Its game's sessions announced in the game itself, where the bot knows how to say something there.
    public bool AnnounceSessions { get; set; } = true;

    public string Address => Port is null ? Host : $"{Host}:{Port}";

    public string JoinAt => JoinAddress ?? Address;
}

public static class PasswordShown
{
    // On the server's card, for everyone who sees it.
    public const string Card = "card";

    // With the card's button, privately, to anyone in the server.
    public const string Anyone = "anyone";

    // With the card's button, privately, to the members of a role.
    public const string Role = "role";
}
