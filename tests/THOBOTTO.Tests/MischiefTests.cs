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
