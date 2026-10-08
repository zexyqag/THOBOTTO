using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

// Stored with SettingsStore under the module id. Points parts apply only while the points module is on.
public sealed record ExpressionRules
{
    [Setting("Voting channel", Help = "Where proposals are voted on; nothing can be proposed until it's set.", Kind = SettingKind.TextChannel)]
    public ulong? VoteChannelId { get; init; }

    [Setting("Cost to propose", Help = "Refunded if accepted.", Unit = "points", Min = 0, Max = 1_000_000)]
    public double ProposeCost { get; init; } = 10;

    [Setting("Bonus when accepted", Help = "For the creator of a new proposal; revivals only get the cost back.", Unit = "points", Min = 0, Max = 1_000_000)]
    public double AcceptBonus { get; init; } = 20;

    [Setting("Votes needed", Help = "Accepted once 👍 lead 👎 by this many.", Min = 1, Max = 1000)]
    public int VoteMargin { get; init; } = 5;

    [Setting("Voting lasts", Unit = "days", Min = 1, Max = 60)]
    public int VoteDays { get; init; } = 3;

    [Setting("Royalty per use", Help = "For the creator each time someone else uses it.", Unit = "points", Min = 0, Max = 1000)]
    public double RoyaltyPerUse { get; init; } = 0.5;

    [Setting("Most royalties a day", Unit = "points", Min = 0, Max = 1_000_000)]
    public double RoyaltyDailyCap { get; init; } = 20;

    [Setting("Retire when unused for", Help = "Deleted, with the image kept so it can be revived.", Unit = "days", Min = 1, Max = 3650)]
    public int RetireAfterDays { get; init; } = 60;

    [Setting("Replace the least used when full", Help = "Otherwise wait for a free slot.")]
    public bool ReplaceLeastUsed { get; init; }
}
