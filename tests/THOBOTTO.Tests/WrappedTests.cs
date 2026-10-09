using NodaTime;

using THOBOTTO.Stats;

namespace THOBOTTO.Tests;

public class WrappedTests
{
    private static readonly DateTimeZone Copenhagen = DateTimeZoneProviders.Tzdb["Europe/Copenhagen"];
    private static readonly Instant Now = Instant.FromUtc(2026, 1, 15, 12, 0);

    [Fact]
    public void Periods_follow_the_server_time_zone()
    {
        var month = WrappedSpan.For(WrappedPeriod.ThisMonth, Copenhagen, Now);
        Assert.Equal("January 2026", month.Label);
        // UTC, as the database needs.
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero), month.From);
        Assert.Equal(TimeSpan.Zero, month.From.Offset);

        var lastMonth = WrappedSpan.For(WrappedPeriod.LastMonth, Copenhagen, Now);
        Assert.Equal("December 2025", lastMonth.Label);
        Assert.Equal(month.From, lastMonth.To);

        Assert.Equal("2025", WrappedSpan.For(WrappedPeriod.LastYear, Copenhagen, Now).Label);
        Assert.Equal("all time", WrappedSpan.For(WrappedPeriod.AllTime, Copenhagen, Now).Label);
    }

    [Fact]
    public void Genres_leave_out_tags_that_arent_genres()
        => Assert.Equal(["hard rock", "rock", "classic rock"], GenreBook.Pick("AC/DC",
        [
            ("hard rock", 100), ("seen live", 90), ("rock", 80), ("AC/DC", 60), ("australian", 50), ("classic rock", 45), ("heavy metal", 40), ("80s", 10),
        ]));
}
