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
}
