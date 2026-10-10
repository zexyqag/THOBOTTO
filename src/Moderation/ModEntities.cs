using THOBOTTO.Access;
using THOBOTTO.Modules;

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
    public const string AutoMod = "automod";

    // The moderator of what Discord's AutoMod did.
    public const ulong AutoModId = 0;

    // Actions that stay in force until lifted or run out.
    public static readonly string[] Lasting = [Timeout, Ban, Lock, Mute, Deafen, RoleAdd, RoleRemove];

    // What lifting a lasting case needs.
    public static string LiftPermission(string type) => type switch
    {
        Ban => BotPermissions.ModBan,
        Lock => BotPermissions.ModChannels,
        Mute or Deafen => BotPermissions.ModVoice,
        RoleAdd or RoleRemove => BotPermissions.ModRoles,
        _ => BotPermissions.ModTimeout,
    };

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
    [Setting("Moderation log", Help = "Where every case is posted. None turns it off.", Kind = SettingKind.TextChannel)]
    public ulong? LogChannelId { get; init; }

    [Setting("DM members", Help = "About warnings, timeouts, kicks and bans, with the reason.")]
    public bool DmMembers { get; init; } = true;

    [Setting("Name the moderator in DMs")]
    public bool DmNamesModerator { get; init; }

    [Setting("Ban appeals", Help = "Where appeals go; the ban DM gets an Appeal button. None turns appeals off.", Kind = SettingKind.TextChannel)]
    public ulong? AppealsChannelId { get; init; }

    [Setting("Appeals open after", Help = "How long after a ban someone can appeal. 0: at once.", Unit = "days", Min = 0, Max = 365)]
    public int AppealAfterDays { get; init; }

    [Setting("Warnings count for", Unit = "days", Min = 1, Max = 3650)]
    public int WarningDays { get; init; } = 30;

    // What happens on its own as warnings add up; none until set.
    public IReadOnlyList<EscalationStep> Escalations { get; init; } = [];

    // Roles the bot's AutoMod filters skip.
    public IReadOnlyList<ulong> AutoModExemptRoleIds { get; init; } = [];
}

// A banned member asking to be let back in, and what the moderators decided.
public sealed class ModAppeal
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    // The ban it's about.
    public int CaseNumber { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public ulong? ChannelId { get; set; }

    public ulong? MessageId { get; set; }

    // Null while waiting; AppealDecisions otherwise.
    public string? Decision { get; set; }

    public ulong? DecidedById { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
}

public static class AppealDecisions
{
    public const string Unbanned = "unbanned";
    public const string Rejected = "rejected";
}
