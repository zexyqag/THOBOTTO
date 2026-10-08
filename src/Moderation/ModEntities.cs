namespace THOBOTTO.Moderation;

public static class CaseTypes
{
    public const string Warn = "warn";
    public const string Note = "note";
    public const string Timeout = "timeout";
    public const string Untimeout = "untimeout";
    public const string Kick = "kick";
    public const string Ban = "ban";
    public const string Unban = "unban";
    public const string Purge = "purge";
    public const string Slowmode = "slowmode";
    public const string Lock = "lock";
    public const string Unlock = "unlock";
    public const string Move = "move";
    public const string Disconnect = "disconnect";
    public const string Mute = "mute";
    public const string Unmute = "unmute";
    public const string Deafen = "deafen";
    public const string Undeafen = "undeafen";
    public const string RoleAdd = "role-add";
    public const string RoleRemove = "role-remove";

    // What a lasting case is undone by.
    public static string? LiftedBy(string type) => type switch
    {
        Timeout => Untimeout,
        Ban => Unban,
        Lock => Unlock,
        Mute => Unmute,
        Deafen => Undeafen,
        RoleAdd => RoleRemove,
        RoleRemove => RoleAdd,
        _ => null,
    };
}

// One moderation action, numbered per server (#12). Kept for good; pardoning or lifting only marks it.
public sealed class ModCase
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public int Number { get; set; }

    public required string Type { get; init; }

    // A member, or for channel actions the channel.
    public ulong TargetId { get; init; }

    // Where it happened, for channel actions and purges; where a member was moved to.
    public ulong? ChannelId { get; init; }

    // The role given or taken.
    public ulong? RoleId { get; init; }

    public ulong ModeratorId { get; init; }

    public string? Reason { get; set; }

    // E.g. the text of the message a warning was about, or what a purge took.
    public string? Details { get; set; }

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
