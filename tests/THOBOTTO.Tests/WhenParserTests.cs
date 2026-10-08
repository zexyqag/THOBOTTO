using NodaTime;

using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class WhenParserTests
{
    // Wednesday 2026-10-07 18:00 in Copenhagen (CEST, UTC+2).
    private static readonly DateTimeZone Copenhagen = DateTimeZoneProviders.Tzdb["Europe/Copenhagen"];
    private static readonly Instant Now = Copenhagen.AtStrictly(new LocalDateTime(2026, 10, 7, 18, 0)).ToInstant();

    private static LocalDateTime Local(string text)
    {
        var when = WhenParser.Parse(text, Copenhagen, Now);
        Assert.True(when.At is not null, when.Problem);
        return when.At!.Value.InZone(Copenhagen).LocalDateTime;
    }

    [Theory]
    [InlineData("20:00", 2026, 10, 7, 20, 0)]       // later today
    [InlineData("17:00", 2026, 10, 8, 17, 0)]       // passed today, so tomorrow
    [InlineData("8pm", 2026, 10, 7, 20, 0)]
    [InlineData("8:30 pm", 2026, 10, 7, 20, 30)]
    [InlineData("12am", 2026, 10, 8, 0, 0)]
    [InlineData("tomorrow 19:30", 2026, 10, 8, 19, 30)]
    [InlineData("tonight 21", 2026, 10, 7, 21, 0)]
    [InlineData("fri 20:00", 2026, 10, 9, 20, 0)]
    [InlineData("Friday at 8pm", 2026, 10, 9, 20, 0)]
    [InlineData("sat 20:00", 2026, 10, 10, 20, 0)]
    [InlineData("wed 20:00", 2026, 10, 7, 20, 0)]     // today, still ahead
    [InlineData("wed 17:00", 2026, 10, 14, 17, 0)]    // today, passed: next week
    [InlineData("next wed 20:00", 2026, 10, 14, 20, 0)]
    [InlineData("2026-12-24 18:00", 2026, 12, 24, 18, 0)]
    [InlineData("24.12 18:00", 2026, 12, 24, 18, 0)]
    [InlineData("24/12/2026 18:00", 2026, 12, 24, 18, 0)]
    [InlineData("1.1 0:00", 2027, 1, 1, 0, 0)]        // passed this year: next year
    [InlineData("10 oct 20:00", 2026, 10, 10, 20, 0)]
    [InlineData("10 October 2026, 20:00", 2026, 10, 10, 20, 0)]
    public void Reads_dates_and_times_in_the_members_zone(string text, int y, int mo, int d, int h, int mi)
    {
        Assert.Equal(new LocalDateTime(y, mo, d, h, mi), Local(text));
    }

    [Theory]
    [InlineData("in 2h", 120)]
    [InlineData("in 90m", 90)]
    [InlineData("in 1d 30m", 1470)]
    [InlineData("in 2 hours", 120)]
    public void Reads_relative_times(string text, int minutes)
    {
        Assert.Equal(Now + Duration.FromMinutes(minutes), WhenParser.Parse(text, Copenhagen, Now).At);
    }

    [Theory]
    [InlineData("friday")]
    [InlineData("whenever 20:00")]
    [InlineData("31.02 20:00")]
    [InlineData("2026-01-01 20:00")]
    [InlineData("25:00")]
    [InlineData("13pm")]
    public void Refuses_what_it_cant_read_or_what_has_passed(string text)
    {
        var when = WhenParser.Parse(text, Copenhagen, Now);
        Assert.Null(when.At);
        Assert.NotNull(when.Problem);
    }

    [Fact]
    public void A_time_in_the_daylight_saving_gap_moves_forward()
    {
        // Clocks jump from 02:00 to 03:00 on 2027-03-28 in Copenhagen.
        Assert.Equal(new LocalDateTime(2027, 3, 28, 3, 30), Local("28.3.2027 2:30"));
    }
}
