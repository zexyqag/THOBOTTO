using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class CurrencyNameTests
{
    [Fact]
    public void Exactly_one_takes_the_name_for_one()
    {
        var rules = new PointRules();
        Assert.Equal("1 point", rules.Format(1));
        Assert.Equal("1 point", rules.Format(1.9));
        Assert.Equal("2 points", rules.Format(2));
        Assert.Equal("0 points", rules.Format(0.4));
        Assert.Equal("point", rules.Name(-1));
        Assert.Equal("points", rules.Name(1.5));
        Assert.Equal("1 🪙 coin", (rules with { CurrencyName = "coins", CurrencyNameOne = "coin", CurrencyEmoji = "🪙" }).Format(1));
    }
}
