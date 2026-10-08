using THOBOTTO.Moderation;

namespace THOBOTTO.Tests;

public class DurationTests
{
    [Theory]
    [InlineData("10m", 600)]
    [InlineData("1h30m", 5400)]
    [InlineData("1h 30m", 5400)]
    [InlineData("2d", 172_800)]
    [InlineData("1W", 604_800)]
    [InlineData("45s", 45)]
    public void Reads_what_moderators_type(string text, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), Durations.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("10")]
    [InlineData("10x")]
    [InlineData("1h and 5m")]
    [InlineData("0m")]
    public void Refuses_the_rest(string text) => Assert.Null(Durations.Parse(text));

    [Theory]
    [InlineData(5400, "1h 30m")]
    [InlineData(777_600, "1w 2d")]
    [InlineData(30, "30s")]
    public void Writes_them_back(int seconds, string text) => Assert.Equal(text, Durations.Format(TimeSpan.FromSeconds(seconds)));
}
