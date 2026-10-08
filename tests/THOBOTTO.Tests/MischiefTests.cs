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

public class PaintTests
{
    [Theory]
    [InlineData("pink", 0xFF69B4, "pink")]
    [InlineData(" Pink ", 0xFF69B4, "pink")]
    [InlineData("#00ff00", 0x00FF00, "#00ff00")]
    [InlineData("00FF00", 0x00FF00, "#00ff00")]
    [InlineData("#000000", 1, "#000000")]     // 0 means "no colour" to Discord
    public void Parses_names_and_hex(string input, int rgb, string name)
    {
        Assert.True(PaintColours.TryParse(input, out var colour, out var parsedName));
        Assert.Equal(rgb, colour.RawValue);
        Assert.Equal(name, parsedName);
    }

    [Theory]
    [InlineData("puce")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    public void Rejects_unknown_colours(string input)
    {
        Assert.False(PaintColours.TryParse(input, out _, out _));
    }

    [Fact]
    public void Paint_costs_per_hour_and_removing_it_early_costs_double_the_rest()
    {
        var rules = new MischiefRules();
        var start = DateTimeOffset.UnixEpoch;
        var paint = new MischiefEffect { Kind = MischiefEffectKinds.Paint, Paid = rules.PaintPrice(4), CreatedAt = start, EndsAt = start.AddHours(4) };

        Assert.Equal(20, paint.Paid);
        Assert.Equal(20, rules.PaintBreakPrice(paint, start.AddHours(2)), 9);
    }
}

public class SelfMischiefTests
{
    [Fact]
    public void Doing_it_to_yourself_costs_five_times_as_much()
    {
        var rules = new MischiefRules();

        Assert.Equal(100, rules.SelfRenamePrice());
        Assert.Equal(50, rules.SelfPaintPrice(2));
    }
}
