using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class SnapshotDiffTests
{
    [Fact]
    public void Members_listed_are_new_back_or_kept_and_the_unlisted_are_gone()
    {
        // 1 stays, 2 left earlier and is back, 3 is still here but unlisted, 4 left earlier and stays away, 5 is new.
        var known = new Dictionary<ulong, bool> { [1] = false, [2] = true, [3] = false, [4] = true };

        var diff = SnapshotDiff.Of(known, [1, 2, 5]);

        Assert.Equal([5], diff.New);
        Assert.Equal([2], diff.Back);
        Assert.Equal([3], diff.Gone);
        Assert.Equal("1 new, 1 back, 1 gone", diff.ToString());
    }

    [Fact]
    public void Nothing_known_and_nothing_listed_changes_nothing()
    {
        var diff = SnapshotDiff.Of(new Dictionary<ulong, bool>(), []);

        Assert.Empty(diff.New);
        Assert.Empty(diff.Back);
        Assert.Empty(diff.Gone);
    }

    [Fact]
    public void A_listing_only_vouches_for_what_it_could_list()
    {
        // 1 is a channel, 2 a thread under a channel whose threads couldn't be listed, 3 a thread under one that could.
        var known = new Dictionary<ulong, bool> { [1] = false, [2] = false, [3] = false };
        var unlisted = new GuildChannelList([], new HashSet<ulong> { 10 });
        var parents = new Dictionary<ulong, (NetCord.ChannelType Type, ulong? Parent)>
        {
            [1] = (NetCord.ChannelType.TextGuildChannel, null),
            [2] = (NetCord.ChannelType.PublicGuildThread, 10),
            [3] = (NetCord.ChannelType.PrivateGuildThread, 11),
        };

        var diff = SnapshotDiff.Of(known, [], id => unlisted.Vouches(parents[id].Type, parents[id].Parent));

        Assert.Equal([1, 3], diff.Gone);
    }

    [Fact]
    public void A_row_seen_or_marked_gone_since_a_listing_began_is_touched()
    {
        var began = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        Assert.False(SnapshotDiff.Touched(began.AddMinutes(-1), null, began));
        Assert.False(SnapshotDiff.Touched(began.AddMinutes(-1), began.AddMinutes(-1), began));
        Assert.True(SnapshotDiff.Touched(began, null, began));
        Assert.True(SnapshotDiff.Touched(began.AddMinutes(-1), began.AddSeconds(5), began));
    }

    [Fact]
    public void A_listing_vouches_for_a_thread_whose_channel_it_listed_in_full()
    {
        var list = new GuildChannelList([], new HashSet<ulong>());

        Assert.True(list.Vouches(NetCord.ChannelType.PublicGuildThread, 10));
        Assert.True(list.Vouches(NetCord.ChannelType.CategoryChannel, null));
        Assert.True(GuildChannelList.IsThread(NetCord.ChannelType.AnnouncementGuildThread));
        Assert.False(GuildChannelList.IsThread(NetCord.ChannelType.ForumGuildChannel));
    }
}
