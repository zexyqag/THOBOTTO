using NodaTime;

using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class RecurrenceTests
{
    private static readonly DateTimeZone Copenhagen = DateTimeZoneProviders.Tzdb["Europe/Copenhagen"];

    [Theory]
    [InlineData("fri", new[] { 5 })]
    [InlineData("mon, thu", new[] { 1, 4 })]
    [InlineData("Friday Saturday", new[] { 5, 6 })]
    [InlineData("weekdays", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("weekends", new[] { 6, 7 })]
    [InlineData("daily", new[] { 1, 2, 3, 4, 5, 6, 7 })]
    public void Parses_days(string text, int[] days)
    {
        Assert.Equal(days, Recurrence.ParseDays(text));
    }

    [Fact]
    public void Rejects_what_isnt_a_day()
    {
        Assert.Null(Recurrence.ParseDays("fri, someday"));
    }

    [Fact]
    public void Lists_occurrences_in_the_window()
    {
        // Wednesday 2026-10-07 18:00; Fridays at 20:00, 10 days ahead.
        var now = Copenhagen.AtStrictly(new LocalDateTime(2026, 10, 7, 18, 0)).ToInstant();
        var upcoming = Recurrence.Upcoming([5], new LocalTime(20, 0), Copenhagen, now, Duration.FromDays(10))
            .Select(i => i.InZone(Copenhagen).LocalDateTime)
            .ToList();

        Assert.Equal([new LocalDateTime(2026, 10, 9, 20, 0), new LocalDateTime(2026, 10, 16, 20, 0)], upcoming);
    }

    [Fact]
    public void Keeps_the_local_time_across_daylight_saving()
    {
        // Clocks go back on Sunday 2026-10-25; Saturday and Monday 20:00 stay 20:00 local.
        var now = Copenhagen.AtStrictly(new LocalDateTime(2026, 10, 24, 12, 0)).ToInstant();
        var upcoming = Recurrence.Upcoming([1, 6], new LocalTime(20, 0), Copenhagen, now, Duration.FromDays(3)).ToList();

        Assert.All(upcoming, i => Assert.Equal(new LocalTime(20, 0), i.InZone(Copenhagen).TimeOfDay));
        Assert.Equal(Duration.FromHours(49), upcoming[1] - upcoming[0]);
    }
}
