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
}
