namespace THOBOTTO.Polls;

// A question asked with /poll in a channel, or in a voice channel's Activity (voted only by those in the call).
public sealed class Poll
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong? MessageId { get; set; }

    public ulong AskerId { get; init; }

    public required string Question { get; init; }

    public required string[] Options { get; init; }

    public bool InVoice { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ClosesAt { get; set; }

    public bool Closed { get; set; }
}

public sealed class PollVote
{
    public long Id { get; init; }

    public long PollId { get; init; }

    public ulong UserId { get; init; }

    public int Option { get; set; }
}
