using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ArchiveSyncTests
{
    private static readonly DateTimeOffset Edited = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_text_and_edit_time_is_unchanged()
    {
        Assert.Equal(SaveStep.Unchanged, ArchiveSync.Decide("hi", null, "hi", null, hasAuthor: true));
        Assert.Equal(SaveStep.Unchanged, ArchiveSync.Decide("hi", Edited, "hi", Edited, hasAuthor: true));
    }

    [Fact]
    public void Different_text_is_an_edit()
        => Assert.Equal(SaveStep.Edited, ArchiveSync.Decide("hi", null, "hello", Edited, hasAuthor: true));

    [Fact]
    public void A_newer_edit_time_with_the_same_text_is_an_edit_too()
    {
        // Removing an attachment edits a message without changing its text.
        Assert.Equal(SaveStep.Edited, ArchiveSync.Decide("hi", null, "hi", Edited, hasAuthor: true));
        Assert.Equal(SaveStep.Edited, ArchiveSync.Decide("hi", Edited, "hi", Edited.AddMinutes(1), hasAuthor: true));
    }

    [Fact]
    public void A_payload_without_text_or_author_is_partial()
    {
        Assert.Equal(SaveStep.Partial, ArchiveSync.Decide("hi", null, null, null, hasAuthor: false));
        // With an author it's a whole message whose text is simply empty.
        Assert.Equal(SaveStep.Edited, ArchiveSync.Decide("hi", null, null, null, hasAuthor: true));
    }

    [Fact]
    public void Ids_known_within_the_pages_range_but_not_fetched_are_missing()
    {
        ulong[] known = [1, 2, 3, 5, 9];

        Assert.Equal([2], ArchiveSync.MissingInRange(known, [1, 3, 5]));
        Assert.Empty(ArchiveSync.MissingInRange(known, [2, 3, 5]));
        Assert.Equal([5], ArchiveSync.MissingInRange(known, [4, 6]));
    }

    [Fact]
    public void Outside_the_range_nothing_is_missing()
    {
        Assert.Empty(ArchiveSync.MissingInRange([1, 9], [3, 4]));
        Assert.Empty(ArchiveSync.MissingInRange([1, 9], []));
        Assert.Empty(ArchiveSync.MissingInRange([], [3, 4]));
    }
}
