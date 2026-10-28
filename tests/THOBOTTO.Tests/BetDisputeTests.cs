using THOBOTTO.Mischief.Bets;

namespace THOBOTTO.Tests;

public class BetDisputeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static Bet Resolved(ulong by, DateTimeOffset at, string state = BetStates.Resolved)
        => new() { CreatorId = 1, Question = "q", Options = ["a", "b"], State = state, WinningOption = 0, ResolvedById = by, ResolvedAt = at };

    [Fact]
    public void A_creators_own_result_can_be_called_unfair_within_the_window()
    {
        Assert.True(BetBook.Disputable(Resolved(1, At), At.AddHours(23), 24));
        Assert.False(BetBook.Disputable(Resolved(1, At), At.AddHours(25), 24));
        Assert.False(BetBook.Disputable(Resolved(2, At), At.AddHours(1), 24));
        Assert.False(BetBook.Disputable(Resolved(1, At), At.AddHours(1), 0));
        Assert.False(BetBook.Disputable(Resolved(1, At, BetStates.Cancelled), At.AddHours(1), 24));
    }
}
