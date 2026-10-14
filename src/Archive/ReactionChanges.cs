namespace THOBOTTO.Archive;

// A reaction as Discord reports it: who, which emoji, and whether it's a super reaction.
public sealed record SeenReaction(ulong UserId, ulong? EmojiId, string EmojiName, bool Burst)
{
    public bool Matches(ArchivedReaction reaction)
        => reaction.UserId == UserId && reaction.Burst == Burst && ReactionChanges.SameEmoji(reaction.EmojiId, reaction.EmojiName, EmojiId, EmojiName);
}

// One emoji's counts on a fetched message.
public sealed record ReactionCount(ulong? EmojiId, string EmojiName, int Normal, int Burst);

public sealed record SeenVote(ulong UserId, int AnswerId)
{
    public bool Matches(ArchivedPollVote vote) => vote.UserId == UserId && vote.AnswerId == AnswerId;
}

// What a reaction event changes, decided over the message's current rows (RemovedAt null) alone: a repeat of
// what's known changes nothing, and a removal of what was never seen records nothing.
public static class ReactionChanges
{
    // A custom emoji is its id (its name can change); a standard one is its name.
    public static bool SameEmoji(ulong? idA, string nameA, ulong? idB, string nameB)
        => idA is { } a ? a == idB : idB is null && nameA == nameB;

    public static ArchivedReaction? Add(IReadOnlyList<ArchivedReaction> current, ulong messageId, SeenReaction seen, DateTimeOffset now)
        => current.Any(seen.Matches) ? null : Row(messageId, seen, now);

    public static ArchivedReaction? Remove(IReadOnlyList<ArchivedReaction> current, SeenReaction seen) => current.FirstOrDefault(seen.Matches);

    public static IReadOnlyList<ArchivedReaction> RemoveEmoji(IReadOnlyList<ArchivedReaction> current, ulong? emojiId, string emojiName)
        => current.Where(r => SameEmoji(r.EmojiId, r.EmojiName, emojiId, emojiName)).ToList();

    // Brings the rows in line with what Discord lists now: what it has that isn't current is added, what was
    // current when the message was asked for and is no longer there is ended. A row added since the asking is
    // newer than the answer, which says nothing about it.
    public static (IReadOnlyList<ArchivedReaction> Added, IReadOnlyList<ArchivedReaction> Gone) Reconcile(
        IReadOnlyList<ArchivedReaction> current, IReadOnlyList<SeenReaction> seen, ulong messageId, DateTimeOffset now, DateTimeOffset askedAt)
    {
        var added = seen.Where(s => !current.Any(s.Matches)).Select(s => Row(messageId, s, now)).ToList();
        var gone = current.Where(r => r.AddedAt < askedAt && !seen.Any(s => s.Matches(r))).ToList();
        return (added, gone);
    }

    // Whether the current rows add up to the counts a fetched message carries; when they don't, Discord is
    // asked who reacted. Rows for an emoji the message no longer lists are stale too.
    public static bool CountsMatch(IReadOnlyList<ArchivedReaction> current, IReadOnlyList<ReactionCount> counts)
    {
        foreach (var count in counts)
        {
            var rows = current.Where(r => SameEmoji(r.EmojiId, r.EmojiName, count.EmojiId, count.EmojiName)).ToList();
            if (rows.Count(r => !r.Burst) != count.Normal || rows.Count(r => r.Burst) != count.Burst)
                return false;
        }
        return current.All(r => counts.Any(c => SameEmoji(r.EmojiId, r.EmojiName, c.EmojiId, c.EmojiName)));
    }

    private static ArchivedReaction Row(ulong messageId, SeenReaction seen, DateTimeOffset now)
        => new() { MessageId = messageId, UserId = seen.UserId, EmojiId = seen.EmojiId, EmojiName = seen.EmojiName, Burst = seen.Burst, AddedAt = now };
}

// The same for a poll's votes.
public static class VoteChanges
{
    public static ArchivedPollVote? Add(IReadOnlyList<ArchivedPollVote> current, ulong messageId, SeenVote seen, DateTimeOffset now)
        => current.Any(seen.Matches) ? null : Row(messageId, seen, now);

    public static ArchivedPollVote? Remove(IReadOnlyList<ArchivedPollVote> current, SeenVote seen) => current.FirstOrDefault(seen.Matches);

    public static (IReadOnlyList<ArchivedPollVote> Added, IReadOnlyList<ArchivedPollVote> Gone) Reconcile(
        IReadOnlyList<ArchivedPollVote> current, IReadOnlyList<SeenVote> seen, ulong messageId, DateTimeOffset now, DateTimeOffset askedAt)
    {
        var added = seen.Where(s => !current.Any(s.Matches)).Select(s => Row(messageId, s, now)).ToList();
        var gone = current.Where(v => v.AddedAt < askedAt && !seen.Any(s => s.Matches(v))).ToList();
        return (added, gone);
    }

    // Whether the current rows add up to a poll's results; an answer the results leave out has no votes.
    public static bool CountsMatch(IReadOnlyList<ArchivedPollVote> current, IReadOnlyList<(int AnswerId, int Count)> counts)
        => counts.All(c => current.Count(v => v.AnswerId == c.AnswerId) == c.Count)
            && current.All(v => counts.Any(c => c.AnswerId == v.AnswerId));

    private static ArchivedPollVote Row(ulong messageId, SeenVote seen, DateTimeOffset now)
        => new() { MessageId = messageId, UserId = seen.UserId, AnswerId = seen.AnswerId, AddedAt = now };
}
