using THOBOTTO.Happenings;

namespace THOBOTTO.Tests;

public class HappeningTests
{
    [Fact]
    public void Each_kind_goes_to_the_forum_by_its_own_switch()
    {
        var rules = new HappeningRules { Bets = false };
        Assert.False(rules.Shows(HappeningKinds.Bet));
        Assert.True(rules.Shows(HappeningKinds.Event));
        Assert.True(rules.Shows(HappeningKinds.Sound));
        Assert.False(rules.Shows("something else"));
    }

    [Fact]
    public void Done_and_cancelled_are_over()
    {
        Assert.True(HappeningStates.Over(HappeningStates.Done));
        Assert.True(HappeningStates.Over(HappeningStates.Cancelled));
        Assert.False(HappeningStates.Over(HappeningStates.Open));
    }

    [Fact]
    public void A_posted_Wrapped_is_known_by_its_period()
    {
        Assert.Equal(202610, Wrapped.WrappedPoster.ThingId("month:2026-10"));
        Assert.Equal(2026, Wrapped.WrappedPoster.ThingId("year:2026"));
        Assert.Equal("October 2026, wrapped up", Wrapped.WrappedPoster.Title("month:2026-10"));
        Assert.Equal("2026, wrapped", Wrapped.WrappedPoster.Title("year:2026"));
        Assert.True(new HappeningRules().Shows(HappeningKinds.Wrapped));
    }

    [Fact]
    public void Records_stay_open_a_while_and_the_rest_until_they_are_over()
    {
        Assert.Equal(TimeSpan.FromDays(7), HappeningKinds.OpenFor(HappeningKinds.Wrapped));
        Assert.Equal(TimeSpan.FromDays(3), HappeningKinds.OpenFor(HappeningKinds.Poll));
        Assert.Equal(TimeSpan.FromDays(3), HappeningKinds.OpenFor(HappeningKinds.Quiz));
        Assert.Null(HappeningKinds.OpenFor(HappeningKinds.Event));
        Assert.Null(HappeningKinds.OpenFor(HappeningKinds.Bet));
        var rules = new HappeningRules { Polls = false };
        Assert.False(rules.Shows(HappeningKinds.Poll));
        Assert.True(rules.Shows(HappeningKinds.Quiz));
    }
}
