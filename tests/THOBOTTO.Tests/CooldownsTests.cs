using THOBOTTO.Modules;

namespace THOBOTTO.Tests;

public class CooldownsTests
{
    [Fact]
    public void Nothing_to_wait_at_first_then_the_rest_of_the_gap_then_nothing_again()
    {
        var cooldowns = new Cooldowns();
        var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, cooldowns.Left(1, 7, at, TimeSpan.FromSeconds(30)));
        cooldowns.Mark(1, 7, at);
        Assert.Equal(TimeSpan.FromSeconds(20), cooldowns.Left(1, 7, at.AddSeconds(10), TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, cooldowns.Left(1, 8, at.AddSeconds(10), TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, cooldowns.Left(1, 7, at.AddSeconds(30), TimeSpan.FromSeconds(30)));
    }
}
