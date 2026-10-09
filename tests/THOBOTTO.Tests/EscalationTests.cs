using THOBOTTO.Moderation;

namespace THOBOTTO.Tests;

public class EscalationTests
{
    private static readonly EscalationStep[] Steps = [new(3, EscalationActions.Timeout, 60), new(5, EscalationActions.Ban, null)];

    [Theory]
    [InlineData(2, null)]
    [InlineData(3, EscalationActions.Timeout)]
    [InlineData(4, null)]
    [InlineData(5, EscalationActions.Ban)]
    [InlineData(6, null)]
    public void A_step_fires_as_the_count_reaches_it(int active, string? action)
        => Assert.Equal(action, Escalation.StepFor(Steps, active)?.Action);

    [Fact]
    public void Steps_read_plainly()
    {
        Assert.Equal("3 warnings → timeout for 1h", Escalation.Describe(Steps[0]));
        Assert.Equal("5 warnings → ban for good", Escalation.Describe(Steps[1]));
    }

    [Fact]
    public void A_step_is_added_in_order_and_replaced_by_its_count()
    {
        var steps = Escalation.With(Steps, 4, EscalationActions.Kick, TimeSpan.FromHours(1));
        Assert.Equal([3, 4, 5], steps.Select(s => s.Warnings));
        Assert.Null(steps[1].Minutes);

        steps = Escalation.With(steps, 3, EscalationActions.Timeout, TimeSpan.FromDays(1));
        Assert.Equal(1440, steps[0].Minutes);
        Assert.Equal(3, steps.Count);
    }

    [Fact]
    public void No_action_removes_the_step()
        => Assert.Equal([5], Escalation.With(Steps, 3, null, null).Select(s => s.Warnings));
}
