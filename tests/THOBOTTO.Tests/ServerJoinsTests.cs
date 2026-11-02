using THOBOTTO.Access;

namespace THOBOTTO.Tests;

public class ServerJoinsTests
{
    [Fact]
    public void Listed_servers_are_ids_whatever_separates_them()
        => Assert.Equal([1557576140221059122UL, 292750105407127553UL, 7UL], ServerJoins.Listed("1557576140221059122, 292750105407127553\n7;oops"));

    [Fact]
    public void Nothing_listed_is_none()
    {
        Assert.Empty(ServerJoins.Listed(null));
        Assert.Empty(ServerJoins.Listed(" , "));
    }

    [Fact]
    public void Only_a_server_it_joined_minutes_ago_was_just_added()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ServerJoins.JustAdded(now.AddSeconds(-20), now));
        Assert.False(ServerJoins.JustAdded(now.AddDays(-30), now));
    }
}
