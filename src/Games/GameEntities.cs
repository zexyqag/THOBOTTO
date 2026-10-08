namespace THOBOTTO.Games;

public sealed class Game
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Name { get; set; }

    public string? Emoji { get; set; }

    // Members who want to hear about the game have this role; sessions ping it.
    public ulong RoleId { get; set; }

    // The game's server on the server board, shown on its sessions.
    public long? ServerId { get; set; }

    // Where its sessions go by default.
    public ulong? ChannelId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

// A role picker message, re-rendered when games change.
public sealed class GamePicker
{
    public ulong MessageId { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }
}
