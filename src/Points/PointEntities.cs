namespace THOBOTTO.Points;

// A member's balance and live activity level. Earnings collect in Pending* and go to the
// ledger once an hour, so the ledger doesn't get a row per member per minute.
public sealed class PointAccount
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public double Balance { get; set; }

    public double Voice { get; set; }

    public double Chat { get; set; }

    public double Received { get; set; }

    public double Given { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastChatAt { get; set; }

    public double PendingEarned { get; set; }

    public double PendingExpired { get; set; }

    public DateTimeOffset? PendingSince { get; set; }
}

public static class PointEntryKinds
{
    public const string Activity = "activity";
    public const string Expired = "expired";
    public const string Adjust = "adjust";
    public const string Spend = "spend";
    public const string Refund = "refund";
}

// The ledger: every change to a balance, append-only.
public sealed class PointEntry
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public double Amount { get; init; }

    public required string Kind { get; init; }

    public string? Reason { get; init; }

    public ulong? ActorId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
