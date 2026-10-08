namespace THOBOTTO.Expressions;

// Stored with SettingsStore under the module id. Points parts apply only while the points module is on.
public sealed record ExpressionRules
{
    // Where proposals are voted on; nothing can be proposed until it's set.
    public ulong? VoteChannelId { get; init; }

    // Paid to propose, refunded if accepted.
    public double ProposeCost { get; init; } = 10;

    // For the creator when a new proposal is accepted; revivals only get the cost back.
    public double AcceptBonus { get; init; } = 20;

    // Accepted once 👍 lead 👎 by this many, within VoteDays.
    public int VoteMargin { get; init; } = 5;
    public int VoteDays { get; init; } = 3;

    // For the creator each time someone else uses it, at most RoyaltyDailyCap a day.
    public double RoyaltyPerUse { get; init; } = 0.5;
    public double RoyaltyDailyCap { get; init; } = 20;

    // Unused this long and it's retired (deleted, image kept for /revive).
    public int RetireAfterDays { get; init; } = 60;

    // With no free slot: retire the least used one to make room, or wait for a slot.
    public bool ReplaceLeastUsed { get; init; }
}
