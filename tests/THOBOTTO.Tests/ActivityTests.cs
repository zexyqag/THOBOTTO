using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class ActivityTests
{
    private static readonly PointRules Rules = new();

    [Fact]
    public void Voice_reaches_about_95_percent_after_three_time_constants()
    {
        var account = new PointAccount();
        PointsEngine.Evolve(account, Rules, inVoice: true, minutes: 3 * Rules.VoiceRiseMinutes);

        Assert.InRange(account.Voice, 0.94 * Rules.VoiceMax, 0.96 * Rules.VoiceMax);
    }

    [Fact]
    public void Voice_ramp_is_the_same_in_one_step_or_many()
    {
        var once = new PointAccount();
        var stepped = new PointAccount();
        PointsEngine.Evolve(once, Rules, inVoice: true, minutes: 60);
        for (var i = 0; i < 60; i++)
            PointsEngine.Evolve(stepped, Rules, inVoice: true, minutes: 1);

        Assert.Equal(once.Voice, stepped.Voice, 9);
    }

    [Fact]
    public void Voice_falls_back_after_leaving()
    {
        var account = new PointAccount { Voice = 1 };
        PointsEngine.Evolve(account, Rules, inVoice: false, minutes: 3 * Rules.VoiceFallMinutes);

        Assert.InRange(account.Voice, 0.04, 0.06);
    }

    [Fact]
    public void Chat_and_reactions_halve_every_half_life()
    {
        var account = new PointAccount { Chat = 0.8, Received = 0.4, Given = 0.2 };
        PointsEngine.Evolve(account, Rules with { ChatHalfLifeMinutes = 10, ReceivedHalfLifeMinutes = 10, GivenHalfLifeMinutes = 10 }, inVoice: false, minutes: 10);

        Assert.Equal(0.4, account.Chat, 9);
        Assert.Equal(0.2, account.Received, 9);
        Assert.Equal(0.1, account.Given, 9);
    }

    [Fact]
    public void Activity_level_is_the_sum_of_components()
    {
        var account = new PointAccount { Voice = 1, Chat = 0.3, Received = 0.1, Given = 0.05 };

        Assert.Equal(1.45, PointsEngine.ActivityLevel(account, Rules), 9);
    }

    [Fact]
    public void Idle_members_get_the_floor()
    {
        var account = new PointAccount { Chat = 0.001 };

        Assert.Equal(-0.1, PointsEngine.ActivityLevel(account, Rules with { IdleFloor = -0.1 }));
    }

    [Theory]
    [InlineData(5, -3, false, 0)]       // a drain stops at zero
    [InlineData(-2, -5, false, -2)]     // an already negative balance doesn't sink further
    [InlineData(-2, 1, false, 1)]       // but can climb
    [InlineData(5, -3, true, -3)]       // unless negatives are allowed
    public void Clamp_keeps_balances_at_or_above_zero_unless_allowed(double before, double after, bool allowNegative, double expected)
    {
        Assert.Equal(expected, PointsEngine.Clamp(after, before, Rules with { AllowNegativeBalance = allowNegative }));
    }
}
