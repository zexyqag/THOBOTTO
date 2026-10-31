namespace THOBOTTO.Live;

// A stream, from when it was seen live to when it stopped: a member's, as Discord showed it on their status, or a
// Twitch channel's the server follows (UserId 0, its name in Streamer).
public sealed class LiveStream
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public string? TwitchId { get; init; }

    public string? Streamer { get; set; }

    // Where: Twitch, YouTube.
    public required string Platform { get; set; }

    public required string Url { get; set; }

    public string? Title { get; set; }

    public string? Game { get; set; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    // The alert, when one was posted.
    public ulong? ChannelId { get; set; }

    public ulong? MessageId { get; set; }
}

// A Twitch channel a server follows, announced when it goes live whether or not its streamer is a member.
public sealed class TwitchChannel
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string TwitchId { get; init; }

    public required string Login { get; set; }

    public required string Name { get; set; }

    public ulong AddedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
