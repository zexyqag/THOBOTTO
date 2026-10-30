namespace THOBOTTO.Live;

// A member's stream, as Discord showed it on their status: from when it was seen live to when it stopped.
public sealed class LiveStream
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

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
