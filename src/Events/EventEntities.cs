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

    public required string Title { get; set; }

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

    // The recurring series this occurrence belongs to, if any.
    public long? SeriesId { get; init; }

    // Set for game sessions.
    public long? GameId { get; init; }

    // VoiceModes.Open or Locked to get a voice channel shortly before the start; null for none.
    public string? VoiceMode { get; init; }

    public ulong? VoiceChannelId { get; set; }

    // Whether to mirror the event as a Discord scheduled event, and its id once created.
    public bool WantsDiscordEvent { get; init; }

    public ulong? DiscordEventId { get; set; }

    // Most who can be in; the rest wait for a spot or another session. Null for no limit.
    public int? Capacity { get; set; }

    // A game session's mode (e.g. "Wingman"), shown by the games module.
    public string? Mode { get; set; }

    // For another session opened because one filled up: the first one, whose title it numbers on.
    public long? FirstPartId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

public static class RsvpStatuses
{
    public const string In = "in";

    // Wanted in while it was full; first come, first moved up.
    public const string Waiting = "waiting";
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

// A regular slot ("every Friday 20:00"); the sweep opens each occurrence as an event ahead of time.
public sealed class EventSeries
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public ulong ChannelId { get; init; }

    public ulong CreatorId { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public ulong? PingRoleId { get; init; }

    // ISO days of the week, Monday = 1.
    public required int[] Days { get; init; }

    // Minutes after local midnight.
    public int TimeOfDay { get; init; }

    // Fixed at creation, so the local time holds across daylight saving changes.
    public required string Zone { get; init; }

    public int OpenDaysAhead { get; init; }

    public string? VoiceMode { get; init; }

    public bool WantsDiscordEvent { get; init; }

    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }
}

public static class VoiceModes
{
    public const string Open = "open";

    // Only those who are In can join.
    public const string Locked = "locked";
}
