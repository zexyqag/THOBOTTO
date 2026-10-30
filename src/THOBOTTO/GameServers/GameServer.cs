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

    public string? Name { get; init; }

    public ulong OwnerId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string Address => Port is null ? Host : $"{Host}:{Port}";
}
