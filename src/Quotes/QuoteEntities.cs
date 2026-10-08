namespace THOBOTTO.Quotes;

// A saved quote: one line or a short exchange, with optional context.
public sealed class Quote
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong AddedById { get; init; }

    public string? Context { get; init; }

    // Set when the quote came from a message, for a link back.
    public ulong? ChannelId { get; init; }

    public ulong? MessageId { get; init; }

    // When it was said: the message time, or when it was saved for voice quotes.
    public DateTimeOffset SaidAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public List<QuoteLine> Lines { get; init; } = [];
}

public sealed class QuoteLine
{
    public long Id { get; init; }

    public long QuoteId { get; init; }

    public int Position { get; init; }

    // A member, or just a name for anyone who isn't one.
    public ulong? SpeakerId { get; init; }

    public string? SpeakerName { get; init; }

    public required string Text { get; init; }
}
