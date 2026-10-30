namespace THOBOTTO.Happenings;

public static class HappeningKinds
{
    public const string Event = "event";
    public const string Session = "session";
    public const string Bet = "bet";
    public const string Emoji = "emoji";
    public const string Sound = "sound";
    public const string Wrapped = "wrapped";
    public const string Poll = "poll";
    public const string Quiz = "quiz";

    // A record posted once it's over (a Wrapped, a poll's or quiz's result) stays open to talk about this long, then
    // it's archived.
    public static TimeSpan? OpenFor(string kind) => kind switch
    {
        Wrapped => TimeSpan.FromDays(7),
        Poll or Quiz => TimeSpan.FromDays(3),
        _ => null,
    };
}

public static class HappeningStates
{
    public const string Open = "open";
    public const string Done = "done";
    public const string Cancelled = "cancelled";

    public static bool Over(string state) => state is Done or Cancelled;
}

// A happening's post in the forum. Its first message, the card, has the post's id.
public sealed class HappeningPost
{
    public long Id { get; init; }

    public ulong GuildId { get; init; }

    public required string Kind { get; init; }

    // The happening's own id (the bet's, the event's, the proposal's; a Wrapped's period, as 202610 or 2026; a poll's
    // or quiz's end, in Unix milliseconds).
    public long ThingId { get; init; }

    public ulong ForumId { get; set; }

    public ulong ThreadId { get; set; }

    public string State { get; set; } = HappeningStates.Open;

    // Deleted on Discord: not posted again.
    public bool Gone { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
