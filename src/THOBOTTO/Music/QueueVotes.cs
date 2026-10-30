namespace THOBOTTO.Music;

public static class QueueVoting
{
    public const string Count = "count";
    public const string Reorder = "reorder";
}

// Votes on what's queued: counted, and with reordering on, the most voted (from the threshold up) go first.
public static class QueueVotes
{
    // The queue with the voted items first, most votes first, each group keeping its order; items under the
    // threshold stay where they are, behind.
    public static List<T> Reordered<T>(IReadOnlyList<T> queue, Func<T, int> votes, int threshold)
    {
        var moved = queue.Where(i => votes(i) >= threshold).OrderByDescending(votes).ToList();
        return [.. moved, .. queue.Where(i => votes(i) < threshold)];
    }
}
