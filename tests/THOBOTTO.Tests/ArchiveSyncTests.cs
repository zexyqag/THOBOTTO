using NetCord;
using NetCord.Rest;

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
    public void An_edit_time_older_than_the_stored_one_is_stale()
    {
        // A page asked before an edit the gateway brought since.
        Assert.Equal(SaveStep.Stale, ArchiveSync.Decide("hello", Edited, "hi", null, hasAuthor: true));
        Assert.Equal(SaveStep.Stale, ArchiveSync.Decide("hello", Edited.AddMinutes(1), "hi", Edited, hasAuthor: true));
        // A partial payload says nothing about edits.
        Assert.Equal(SaveStep.Partial, ArchiveSync.Decide("hello", Edited, null, null, hasAuthor: false));
    }

    [Fact]
    public void A_payload_without_text_or_author_is_partial()
    {
        Assert.Equal(SaveStep.Partial, ArchiveSync.Decide("hi", null, null, null, hasAuthor: false));
        // With an author it's a whole message whose text is simply empty.
        Assert.Equal(SaveStep.Edited, ArchiveSync.Decide("hi", null, null, null, hasAuthor: true));
    }

    // Ids are times; these are from well before Edited, the moment the pages were asked.
    private const ulong Old = 1_000_000_000_000_000_000;

    private static readonly ulong Newest = Snowflake.Create(Edited.AddMinutes(-1));

    [Fact]
    public void A_full_page_after_a_cursor_vouches_from_the_cursor_to_its_last_id()
        => Assert.Equal((Old + 1, Old + 70), ArchiveSync.Covered(PaginationDirection.After, Old, [Old + 20, Old + 70, Old + 40], full: true, Edited));

    [Fact]
    public void A_short_page_after_a_cursor_vouches_to_the_moment_it_was_asked_less_a_minute()
    {
        Assert.Equal((Old + 1, Newest), ArchiveSync.Covered(PaginationDirection.After, Old, [Old + 20], full: false, Edited));
        Assert.Equal((Old + 1, Newest), ArchiveSync.Covered(PaginationDirection.After, Old, [], full: false, Edited));
        Assert.Equal((1UL, Newest), ArchiveSync.Covered(PaginationDirection.After, 0, [], full: false, Edited));
    }

    [Fact]
    public void A_page_before_a_cursor_vouches_from_its_first_id_or_the_channels_start_to_the_cursor()
    {
        Assert.Equal((Old + 20, Old + 99), ArchiveSync.Covered(PaginationDirection.Before, Old + 100, [Old + 70, Old + 20], full: true, Edited));
        Assert.Equal((0UL, Old + 99), ArchiveSync.Covered(PaginationDirection.Before, Old + 100, [Old + 70, Old + 20], full: false, Edited));
        Assert.Equal((0UL, Old + 99), ArchiveSync.Covered(PaginationDirection.Before, Old + 100, [], full: false, Edited));
    }

    [Fact]
    public void The_newest_page_vouches_to_the_moment_it_was_asked()
    {
        Assert.Equal((Old + 20, Newest), ArchiveSync.Covered(PaginationDirection.Before, null, [Old + 70, Old + 20], full: true, Edited));
        Assert.Equal((0UL, Newest), ArchiveSync.Covered(PaginationDirection.Before, null, [Old + 20], full: false, Edited));
    }
}
