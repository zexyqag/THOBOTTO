namespace THOBOTTO.Events;

public static class EventStates
{
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";

    // Past its start by EndAfterHours: RSVPs closed.
    public const string Over = "over";
}

public sealed class Event
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong? MessageId { get; set; }

    public ulong CreatorId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    // Pinged when the event is announced.
    public ulong? PingRoleId { get; init; }

    // Null while the time is being voted on.
    public DateTimeOffset? StartsAt { get; set; }

    // Set for events whose time is put to a vote; it's decided at this moment at the latest.
    public DateTimeOffset? PollClosesAt { get; set; }

    // In a poll: whether members may add times.
    public bool AllowProposals { get; init; }

    public string State { get; set; } = EventStates.Scheduled;

    public bool ReminderSent { get; set; }

    public bool StartSent { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class RsvpStatuses
{
    public const string In = "in";
    public const string Maybe = "maybe";
    public const string Out = "out";
}

public sealed class EventRsvp
{
    public long EventId { get; init; }

    public ulong UserId { get; init; }

    public required string Status { get; set; }

    public DateTimeOffset At { get; set; }
}

// A member's own time zone, for reading the times they type. Not per guild: it's where they are.
public sealed class MemberTimeZone
{
    public ulong UserId { get; init; }

    public required string Zone { get; set; }
}

// A candidate time in a poll.
public sealed class EventTimeOption
{
    public long Id { get; init; }

    public long EventId { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public ulong ProposedById { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class EventTimeVote
{
    public long OptionId { get; init; }

    public ulong UserId { get; init; }
}
