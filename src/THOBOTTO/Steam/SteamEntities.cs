namespace THOBOTTO.Steam;

// A member's Steam account, for every server, linked by signing in through Steam.
public sealed class SteamLink
{
    public ulong UserId { get; init; }

    public ulong SteamId { get; set; }

    // Their Steam name when last read.
    public required string Name { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    // When their games were last read; Hidden: their game details aren't public on Steam, so none are kept.
    public DateTimeOffset? ReadAt { get; set; }

    public bool Hidden { get; set; }
}

// A game a member owns on Steam, as last read.
public sealed class SteamGame
{
    public ulong UserId { get; init; }

    public int AppId { get; init; }

    public required string Name { get; set; }

    public int Minutes { get; set; }
}

// Whether a game can be played together, as its store page says; kept, as the store answers slowly and only so often.
public sealed class SteamApp
{
    public int AppId { get; init; }

    // Null: the store has no page for it.
    public bool? Together { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}
