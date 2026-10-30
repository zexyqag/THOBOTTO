namespace THOBOTTO.Events;

public static class PollMath
{
    // The option with most votes, ties going to the earliest; options already past don't count.
    // Null when nobody voted for anything still ahead.
    public static EventTimeOption? Winner(IReadOnlyList<EventTimeOption> options, IReadOnlyList<EventTimeVote> votes, DateTimeOffset now)
        => options
            .Where(o => o.StartsAt > now)
            .Select(o => (Option: o, Votes: votes.Count(v => v.OptionId == o.Id)))
            .Where(o => o.Votes > 0)
            .OrderByDescending(o => o.Votes)
            .ThenBy(o => o.Option.StartsAt)
            .Select(o => o.Option)
            .FirstOrDefault();
}
