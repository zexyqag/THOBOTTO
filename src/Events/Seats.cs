namespace THOBOTTO.Events;

// Who gets a spot in an event with a limit: first come first served, the rest wait in order.
public static class Seats
{
    public static bool HasRoom(int inCount, int? capacity) => capacity is not { } c || inCount < c;

    // What an RSVP becomes: In while there's room (or already in), else Waiting, keeping one's place.
    public static string Status(string requested, string? was, int inCount, int? capacity)
        => requested != RsvpStatuses.In || was == RsvpStatuses.In ? requested
            : was == RsvpStatuses.Waiting || !HasRoom(inCount, capacity) ? RsvpStatuses.Waiting
            : RsvpStatuses.In;

    // Voters for a poll's winning time, in order: the first ones in, the rest waiting.
    public static (List<ulong> Going, List<ulong> Waiting) Split(IReadOnlyList<ulong> voted, int? capacity)
    {
        var going = voted.Take(capacity ?? int.MaxValue).ToList();
        return (going, voted.Skip(going.Count).ToList());
    }

    // Who moves to another session of a full event: whoever opened it, unless they have a spot or
    // are waiting already, then the waiting list in order, up to the limit.
    public static List<ulong> MovingToAnother(IReadOnlyList<EventRsvp> byTime, ulong opener, int capacity)
    {
        var openerHasPlace = byTime.Any(r => r.UserId == opener && r.Status is RsvpStatuses.In or RsvpStatuses.Waiting);
        return (openerHasPlace ? [] : new[] { opener })
            .Concat(byTime.Where(r => r.Status == RsvpStatuses.Waiting).Select(r => r.UserId))
            .Take(capacity)
            .ToList();
    }
}
