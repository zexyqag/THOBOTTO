using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class DeletionTests
{
    private const ulong Author = 10, Channel = 20, Mod = 30, OtherMod = 31;

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 18, 0, 0, TimeSpan.Zero);

    // An entry id made at a given moment, as Discord makes them.
    private static ulong IdAt(DateTimeOffset at) => (ulong)(at.ToUnixTimeMilliseconds() - 1_420_070_400_000) << 22;

    private static DeleteEntry Entry(DateTimeOffset at, ulong by, int count = 1, ulong target = Author, ulong channel = Channel)
        => new(IdAt(at), by, target, channel, count);

    private static bool Fits(DeleteEntry e) => e.TargetId == Author && e.ChannelId == Channel;

    [Fact]
    public void Ids_tell_their_time()
        => Assert.Equal(Now, DeletionClues.CreatedAt(IdAt(Now)));

    [Fact]
    public void A_new_entry_names_the_deleter()
    {
        var old = Entry(Now.AddHours(-1), OtherMod);
        var fresh = Entry(Now.AddSeconds(-3), Mod);
        var seen = new Dictionary<ulong, int> { [old.Id] = 1 };
        Assert.Equal(Mod, DeletionClues.Match([fresh, old], seen, Fits, Now, Author));
    }

    [Fact]
    public void A_folded_repeat_is_found_by_its_count_going_up()
    {
        var entry = Entry(Now.AddMinutes(-2), Mod, count: 3);
        var seen = new Dictionary<ulong, int> { [entry.Id] = 2 };
        Assert.Equal(Mod, DeletionClues.Match([entry], seen, Fits, Now, Author));
    }

    [Fact]
    public void Nothing_new_means_the_author_deleted_it()
    {
        var entry = Entry(Now.AddMinutes(-2), Mod, count: 2);
        var seen = new Dictionary<ulong, int> { [entry.Id] = 2 };
        Assert.Equal(Author, DeletionClues.Match([entry], seen, Fits, Now, Author));
    }

    [Fact]
    public void Entries_for_another_author_or_channel_dont_count()
    {
        var seen = new Dictionary<ulong, int>();
        var elsewhere = Entry(Now.AddSeconds(-3), Mod, channel: 99);
        var someoneElse = Entry(Now.AddSeconds(-3), Mod, target: 98);
        Assert.Equal(Author, DeletionClues.Match([elsewhere, someoneElse], seen, Fits, Now, Author));
    }

    [Fact]
    public void Just_started_only_a_fresh_entry_is_trusted()
    {
        var folded = Entry(Now.AddMinutes(-2), Mod, count: 4);
        Assert.Null(DeletionClues.Match([folded], null, Fits, Now, Author));
        Assert.Equal(Mod, DeletionClues.Match([Entry(Now.AddSeconds(-3), Mod)], null, Fits, Now, Author));
    }

    [Fact]
    public void A_bulk_deletion_without_an_entry_stays_unknown()
        => Assert.Null(DeletionClues.Match([], new Dictionary<ulong, int>(), e => e.TargetId == Channel, Now, selfDelete: null));
}
