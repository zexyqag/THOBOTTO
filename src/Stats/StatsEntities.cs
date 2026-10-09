namespace THOBOTTO.Stats;

// A song a helper played, for Wrapped and listening stats.
public sealed class PlayRecord
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong VoiceChannelId { get; init; }

    public ulong HelperId { get; init; }

    // The personality the helper wore then, if any.
    public string? Personality { get; init; }

    public required string Artist { get; init; }

    public required string Title { get; init; }

    public required string Source { get; init; }

    public string? Uri { get; init; }

    public long LengthMs { get; init; }

    public long PlayedMs { get; init; }

    // Ended well before its end: skipped or stopped.
    public bool Skipped { get; init; }

    // Queued by autoplay rather than a member.
    public bool Autoplay { get; init; }

    public ulong RequestedBy { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}

// Someone who heard a played song (there at its start or its end, not deafened).
public sealed class PlayListener
{
    public long PlayId { get; init; }

    public ulong UserId { get; init; }
}

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
