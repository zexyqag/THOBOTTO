using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class CalendarTests
{
    [Fact]
    public void Text_is_escaped_as_calendars_want_it()
        => Assert.Equal(@"Game night\, again\; bring snacks\\drinks\nsecond line", Calendars.Escape("Game night, again; bring snacks\\drinks\nsecond line"));

    [Fact]
    public void Long_lines_go_on_in_the_next_without_splitting_a_letter()
    {
        var folded = Calendars.Fold("SUMMARY:" + new string('a', 66) + "ææææ");
        var lines = folded.Split("\r\n");
        Assert.Equal(2, lines.Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[0]) <= 75);
        Assert.StartsWith(" ", lines[1]);
        Assert.Equal("SUMMARY:" + new string('a', 66) + "ææææ", lines[0] + lines[1][1..]);
        Assert.Equal("SHORT:x", Calendars.Fold("SHORT:x"));
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
