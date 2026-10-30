namespace THOBOTTO.Fame;

// One person's reaction with one emoji on a message that may make the hall of fame.
public sealed class FameReaction
{
    public ulong MessageId { get; init; }

    public ulong ReactorId { get; init; }

    public required string Emoji { get; init; }

    public ulong GuildId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

// A message in the hall of fame. Inserted first to claim it, so it is inducted once.
public sealed class FameEntry
{
    public ulong MessageId { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong AuthorId { get; set; }

    public int Reactors { get; set; }

    // The forwarded copy in the showcase; null if it couldn't be posted.
    public ulong? ShowcaseMessageId { get; set; }

    public DateTimeOffset InductedAt { get; init; }
}
