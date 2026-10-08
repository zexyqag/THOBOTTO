namespace THOBOTTO.Moderation;

public static class CaseTypes
{
    public const string Warn = "warn";
    public const string Note = "note";
}

// One moderation action, numbered per server (#12). Kept for good; pardoning or lifting only marks it.
public sealed class ModCase
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public int Number { get; set; }

    public required string Type { get; init; }

    public ulong TargetId { get; init; }

    public ulong ModeratorId { get; init; }

    public string? Reason { get; set; }

    // E.g. the text of the message a warning was about.
    public string? Details { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // For actions that last a while: when it ends on its own, and when it actually ended.
    public DateTimeOffset? EndsAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    // A pardoned warning no longer counts; it stays in the history.
    public DateTimeOffset? PardonedAt { get; set; }

    public ulong? PardonedById { get; set; }

    public string? PardonReason { get; set; }

    // Its post in the moderation log, kept up to date.
    public ulong? LogChannelId { get; set; }

    public ulong? LogMessageId { get; set; }
}

public sealed record ModRules
{
    // Where every case is posted; none if null.
    public ulong? LogChannelId { get; init; }

    // DM members about warnings, timeouts, kicks and bans.
    public bool DmMembers { get; init; } = true;

    // Name the moderator in those DMs.
    public bool DmNamesModerator { get; init; }

    // How long a warning counts.
    public int WarningDays { get; init; } = 30;
}
