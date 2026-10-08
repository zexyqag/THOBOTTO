namespace THOBOTTO.Expressions;

public static class ExpressionKinds
{
    public const string Emoji = "emoji";
    public const string Sticker = "sticker";
}

public static class ExpressionStates
{
    public const string Voting = "voting";
    public const string Rejected = "rejected";

    // Accepted, waiting for a free slot.
    public const string Waiting = "waiting";

    public const string Live = "live";
    public const string Retired = "retired";
}

// A member-made emoji or sticker, from proposal to retirement. The image is kept so it can be revived.
public sealed class Expression
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    // Stickers only: the emoji Discord suggests it for.
    public string? Tag { get; init; }

    // Who made it; royalties go here.
    public ulong CreatorId { get; init; }

    // Who put it up for this vote: the creator, or whoever revived it.
    public ulong ProposerId { get; init; }

    // The retired expression this one brings back, if it's a revival.
    public long? RevivedFromId { get; init; }

    public required byte[] Image { get; init; }

    public required string ContentType { get; init; }

    public bool Animated { get; init; }

    public string State { get; set; } = ExpressionStates.Voting;

    public double Paid { get; init; }

    public ulong? VoteChannelId { get; set; }

    public ulong? VoteMessageId { get; set; }

    public DateTimeOffset ProposedAt { get; init; }

    public DateTimeOffset VoteEndsAt { get; init; }

    public DateTimeOffset? DecidedAt { get; set; }

    // The emoji or sticker on Discord while live.
    public ulong? DiscordId { get; set; }

    public DateTimeOffset? LiveAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public int Uses { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class ExpressionVote
{
    public long ExpressionId { get; init; }

    public ulong UserId { get; init; }

    public bool Up { get; set; }
}
