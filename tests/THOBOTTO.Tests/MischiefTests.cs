using THOBOTTO.Mischief;

namespace THOBOTTO.Tests;

public class MischiefTests
{
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 40)]
    [InlineData(3, 160)]
    public void Rename_price_doubles_per_recent_rename(int recent, double expected)
    {
        Assert.Equal(expected, new MischiefRules().RenamePrice(recent));
    }
}

public class MischiefPriceTests
{
    private static readonly MischiefRules Rules = new();

    [Theory]
    [InlineData(0, 40)]     // right after: full price
    [InlineData(12, 20)]    // halfway through the window
    [InlineData(23, 5)]     // near the end: the minimum, not less
    [InlineData(100, 5)]
    public void Buy_back_gets_cheaper_as_the_rename_ages(double hours, double expected)
    {
        Assert.Equal(expected, Rules.BuyBackPrice(TimeSpan.FromHours(hours)), 9);
    }

    [Fact]
    public void Breaking_a_lock_costs_twice_the_value_of_the_time_left()
    {
        var start = DateTimeOffset.UnixEpoch;
        var nameLock = new MischiefEffect { Kind = MischiefEffectKinds.Lock, Paid = 40, CreatedAt = start, EndsAt = start.AddHours(4) };

        Assert.Equal(80, Rules.LockBreakPrice(nameLock, start), 9);
        Assert.Equal(20, Rules.LockBreakPrice(nameLock, start.AddHours(3)), 9);
        Assert.Equal(0, Rules.LockBreakPrice(nameLock, start.AddHours(5)), 9);
    }
}
