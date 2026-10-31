using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class CalendarTests
{
    [Fact]
    public void A_calendar_reads_back_as_written_with_its_reminders_and_cancellations()
    {
        var start = new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(1));
        var long_ = "Game night, again; bring snacks\\drinks\nsecond line " + new string('a', 60) + " ææææ 🎉";
        var ics = Calendars.Ics("Bottesting events",
        [
            new("event-1-9@thobotto", start, TimeSpan.FromMinutes(90), "✅ Game night", long_, "https://discord.com/channels/9/8/7", false, 30, "Game night"),
            new("event-2-9@thobotto", start.AddDays(1), TimeSpan.FromHours(2), "Called off", "", null, true, null, "Called off"),
        ], start);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("X-WR-CALNAME:Bottesting events", ics);
        Assert.Contains("REFRESH-INTERVAL;VALUE=DURATION:PT1H", ics);
        Assert.Contains("TRIGGER:-PT30M", ics);

        var read = Ical.Net.Calendar.Load(ics)!;
        var night = read.Events.Single(e => e.Uid == "event-1-9@thobotto");
        Assert.Equal(("✅ Game night", long_, "https://discord.com/channels/9/8/7", "CONFIRMED"), (night.Summary, night.Description, night.Url?.ToString(), night.Status));
        Assert.Equal(new DateTime(2026, 10, 30, 17, 0, 0, DateTimeKind.Utc), night.DtStart!.AsUtc);
        Assert.Equal(new DateTime(2026, 10, 30, 18, 30, 0, DateTimeKind.Utc), night.DtEnd!.AsUtc);
        Assert.Equal("Game night", Assert.Single(night.Alarms).Description);
        var off = read.Events.Single(e => e.Uid == "event-2-9@thobotto");
        Assert.Equal("CANCELLED", off.Status);
        Assert.Empty(off.Alarms);
    }

    [Fact]
    public void Times_are_written_in_utc()
        => Assert.Equal("20261030T170000Z", Calendars.Stamp(new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.FromHours(1))));

    [Fact]
    public void An_event_lasts_its_own_length_or_the_usual_one()
    {
        Assert.Equal((90, null), EventRules.ReadLength("90m"));
        Assert.Equal((90, null), EventRules.ReadLength("1h30m"));
        Assert.Equal((120, null), EventRules.ReadLength("2h"));
        Assert.Equal((null, null), EventRules.ReadLength(" "));
        Assert.NotNull(EventRules.ReadLength("10m").Problem);
        Assert.NotNull(EventRules.ReadLength("2d").Problem);
        Assert.NotNull(EventRules.ReadLength("soon").Problem);
        var rules = new EventRules { UsualMinutes = 120 };
        Assert.Equal(TimeSpan.FromHours(2), rules.LengthOf(new Event { Title = "x" }));
        Assert.Equal(TimeSpan.FromMinutes(45), rules.LengthOf(new Event { Title = "x", LengthMinutes = 45 }));
    }
}
