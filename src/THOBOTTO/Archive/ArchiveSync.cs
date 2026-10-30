using NetCord;
using NetCord.Rest;

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
    // An edit time older than the stored one: an answer asked before an edit the gateway brought since; nothing taken.
    Stale,
}

// The decisions of reconciling the archive with what Discord says, apart from the database.
public static class ArchiveSync
{
    public static SaveStep Decide(string? storedContent, DateTimeOffset? storedEditedAt, string? content, DateTimeOffset? editedAt, bool hasAuthor)
    {
        if (content is null && !hasAuthor)
            return SaveStep.Partial;
        if (storedEditedAt is { } stored && (editedAt is null || editedAt < stored))
            return SaveStep.Stale;
        return content != storedContent || editedAt != storedEditedAt ? SaveStep.Edited : SaveStep.Unchanged;
    }

    // The clocks here and at Discord may differ a little; ids made within this of a moment aren't vouched for.
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    // The id range a fetched page vouches for, both ends included: from the cursor it was asked from to its far
    // end, and when the page was short, to the end of the channel in that direction: nothing older, or nothing
    // newer than the moment it was asked (ids being times). A known message in the range Discord didn't return
    // was deleted while the bot wasn't looking.
    public static (ulong From, ulong To) Covered(PaginationDirection direction, ulong? cursor, IReadOnlyCollection<ulong> fetched, bool full, DateTimeOffset askedAt)
    {
        var newest = Snowflake.Create(askedAt - Slack);
        if (direction == PaginationDirection.After && cursor is { } after)
            return (after + 1, full ? fetched.Max() : newest);
        var from = full ? fetched.Min() : 0;
        return (from, cursor is { } before and > 0 ? before - 1 : newest);
    }
}
