namespace THOBOTTO.Mischief.Bets;

public static class BetStates
{
    // Taking stakes until ClosesAt, then waiting for a result.
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Cancelled = "cancelled";
}

public sealed class Bet
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong? MessageId { get; set; }

    public ulong CreatorId { get; init; }

    public required string Question { get; init; }

    public required string[] Options { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ClosesAt { get; set; }

    public string State { get; set; } = BetStates.Open;

    // Index into Options.
    public int? WinningOption { get; set; }

    public ulong? ResolvedById { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    // Whether the message already says betting has closed.
    public bool ClosedShown { get; set; }

    // Rules at creation, so changing the settings never changes a running bet.
    public double CreatorCutPercent { get; init; }

    public double HouseCutPercent { get; init; }

    public bool CreatorCanBet { get; init; }
}

public sealed class BetStake
{
    public long Id { get; init; }

    public long BetId { get; init; }

    public ulong UserId { get; init; }

    public int Option { get; init; }

    public double Amount { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class BetPayoutKinds
{
    public const string Win = "win";
    public const string Cut = "cut";
    public const string Refund = "refund";
}

// What a resolution or cancellation paid out, so a revert can take back exactly that.
public sealed class BetPayout
{
    public long Id { get; init; }

    public long BetId { get; init; }

    public ulong UserId { get; init; }

    public double Amount { get; init; }

    public required string Kind { get; init; }

    public bool Reversed { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
