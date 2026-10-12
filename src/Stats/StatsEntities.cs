namespace THOBOTTO.Stats;

// A member's time in one voice channel.
public sealed class VoiceSession
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public ulong ChannelId { get; init; }

    public DateTimeOffset JoinedAt { get; init; }

    public DateTimeOffset? LeftAt { get; set; }

    // Updated while open, so a session cut short by a crash ends about when the bot went down.
    public DateTimeOffset SeenAt { get; set; }
}

// A member who wants to be named in this server's Wrapped (everyone counts in its totals).
public sealed class WrappedOptIn
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public DateTimeOffset At { get; init; }
}

// A Wrapped the bot posted on its own ("month:2026-09", "year:2026"), so it's posted once.
public sealed class WrappedPost
{
    public ulong GuildId { get; init; }

    public required string Key { get; init; }

    public DateTimeOffset At { get; init; }
}
