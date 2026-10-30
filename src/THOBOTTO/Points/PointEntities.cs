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

    // Interactions: votes, quotes, coming to events, watch parties, quiz wins.
    public double Interactions { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastChatAt { get; set; }

    public double PendingEarned { get; set; }

    public double PendingExpired { get; set; }

    public DateTimeOffset? PendingSince { get; set; }
}

// The ledger's own kinds; a module awarding points names its own ("fame", "bet").
public static class PointEntryKinds
{
    public const string Activity = "activity";
    public const string Expired = "expired";
    public const string Adjust = "adjust";
    public const string Spend = "spend";
    public const string Refund = "refund";
    public const string Kudos = "kudos";

    // What an entry was, e.g. "kudos from @x: thanks"; how a member is named is up to the caller.
    public static string Describe(PointEntry e, Func<ulong, string> name)
    {
        var what = e.Kind == Kudos && e.ActorId is { } other ? $"kudos {(e.Amount < 0 ? "to" : "from")} {name(other)}" : e.Kind;
        return e.Reason is null ? what : $"{what}: {e.Reason}";
    }
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

    // Who caused it (an adjustment's admin); for kudos, the other member.
    public ulong? ActorId { get; init; }

    // Earned by doing something (activity, kudos received, a bonus or a win), as opposed to points moved around
    // (bets, refunds, adjustments, spending).
    public bool Earned { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
