using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class InteractionActivityTests
{
    [Fact]
    public void Interactions_count_toward_the_level_and_fade_by_their_half_life()
    {
        var rules = new PointRules { InteractionsHalfLifeMinutes = 60, IdleFloor = 0 };
        var account = new PointAccount { GuildId = 1, UserId = 2, Interactions = 0.8 };
        Assert.Equal(0.8, PointsEngine.ActivityLevel(account, rules), 3);
        PointsEngine.Evolve(account, rules, inVoice: false, minutes: 60);
        Assert.Equal(0.4, account.Interactions, 3);
        PointsEngine.Evolve(account, rules, inVoice: false, minutes: 60);
        Assert.Equal(0.2, account.Interactions, 3);
    }
}
