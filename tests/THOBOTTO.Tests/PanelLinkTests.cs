using THOBOTTO.Panel;

namespace THOBOTTO.Tests;

public class PanelLinkTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_link_logs_in_once()
    {
        var links = new PanelLinks(null!, new Clock());
        var token = links.Issue(1, "Ana", 7);
        var first = links.Redeem(token);
        Assert.Equal(1UL, first!.UserId);
        Assert.Equal(7UL, first.GuildId);
        Assert.Null(links.Redeem(token));
    }

    [Fact]
    public void A_link_expires()
    {
        var clock = new Clock();
        var links = new PanelLinks(null!, clock);
        var token = links.Issue(1, "Ana", null);
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Null(links.Redeem(token));
    }

    [Fact]
    public void Made_up_links_log_in_nobody()
        => Assert.Null(new PanelLinks(null!, new Clock()).Redeem("nope"));
}
