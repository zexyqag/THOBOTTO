namespace THOBOTTO.Gate;

public static class HoldReasons
{
    public const string Raid = "raid";
    public const string Young = "young";
    public const string Verify = "verify";
}

// A member in the waiting room, and why.
public sealed class GateHold
{
    public ulong GuildId { get; init; }

    public ulong UserId { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset Since { get; init; }

    // Its post in the gate channel, with the Let in / Kick buttons.
    public ulong? ReviewChannelId { get; set; }

    public ulong? ReviewMessageId { get; set; }
}

// A raid going on: since when, the last join, and what to set back when it ends.
public sealed class GateRaid
{
    public ulong GuildId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastJoinAt { get; set; }

    public int? PreviousVerificationLevel { get; set; }

    public bool InvitesPaused { get; set; }
}
