using THOBOTTO.Anonymous;

namespace THOBOTTO.Tests;

public class AnonymousTests
{
    [Fact]
    public void A_member_keeps_their_number_for_the_day_and_gets_a_new_one_the_next()
    {
        var time = new Clock { Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero) };
        var channels = new AnonymousChannels(null!, time);
        var rules = new AnonymousRules();

        Assert.Equal("Anonymous 1", channels.Of(1, 10, rules).Name);
        Assert.Equal("Anonymous 2", channels.Of(1, 20, rules).Name);
        Assert.Equal("Anonymous 1", channels.Of(1, 10, rules).Name);
        Assert.Equal("Anonymous 1", channels.Of(2, 20, rules).Name);
        time.Now += TimeSpan.FromDays(1);
        Assert.Equal("Anonymous 1", channels.Of(1, 20, rules).Name);
        Assert.Equal("Anonymous", channels.Of(1, 10, rules with { Naming = AnonymousNaming.Plain }).Name);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
