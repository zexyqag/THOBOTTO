using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class DailyAllowanceTests
{
    [Fact]
    public void The_first_ones_of_a_day_are_free_then_none_until_the_next_day()
    {
        var allowance = new DailyAllowance();
        var monday = new DateTimeOffset(2026, 10, 26, 23, 50, 0, TimeSpan.Zero);
        Assert.True(allowance.Take(1, 7, monday, 2));
        Assert.True(allowance.Take(1, 7, monday, 2));
        Assert.False(allowance.Take(1, 7, monday, 2));
        Assert.True(allowance.Take(1, 8, monday, 2));
        Assert.True(allowance.Take(1, 7, monday.AddMinutes(15), 2));
        Assert.False(allowance.Take(1, 7, monday, 0));
    }
}
