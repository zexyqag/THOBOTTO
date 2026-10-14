namespace THOBOTTO.Archive;

// What a message that is already archived gets from a payload about it.
public enum SaveStep
{
    // The same text and edit time: the raw JSON is refreshed (reactions, pins, embeds as they are now).
    Unchanged,
    // Different text or edit time: the old version is kept and the new one takes its place.
    Edited,
    // A payload with neither text nor author, the fields it carries written over the stored JSON.
    Partial,
}

// The decisions of reconciling the archive with what Discord says, apart from the database.
public static class ArchiveSync
{
    public static SaveStep Decide(string? storedContent, DateTimeOffset? storedEditedAt, string? content, DateTimeOffset? editedAt, bool hasAuthor)
    {
        if (content is null && !hasAuthor)
            return SaveStep.Partial;
        return content != storedContent || editedAt != storedEditedAt ? SaveStep.Edited : SaveStep.Unchanged;
    }

    // The known ids within a fetched page's range that Discord no longer returned: deleted while the bot
    // wasn't looking. Outside the range nothing can be told.
    public static IReadOnlyList<ulong> MissingInRange(IEnumerable<ulong> known, IReadOnlyCollection<ulong> fetched)
    {
        if (fetched.Count == 0)
            return [];
        var min = fetched.Min();
        var max = fetched.Max();
        var fetchedSet = fetched as IReadOnlySet<ulong> ?? fetched.ToHashSet();
        return known.Where(id => id >= min && id <= max && !fetchedSet.Contains(id)).Order().ToList();
    }
}
