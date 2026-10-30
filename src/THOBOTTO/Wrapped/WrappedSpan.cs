using NetCord.Services.ApplicationCommands;

using NodaTime;

namespace THOBOTTO.Wrapped;

public enum WrappedPeriod
{
    [SlashCommandChoice(Name = "This month")] ThisMonth,
    [SlashCommandChoice(Name = "Last month")] LastMonth,
    [SlashCommandChoice(Name = "This year")] ThisYear,
    [SlashCommandChoice(Name = "Last year")] LastYear,
    [SlashCommandChoice(Name = "All time")] AllTime,
}

// A stretch of time in a zone, with how to say it ("October 2026", "2026", "all time").
public sealed record WrappedSpan(string Label, DateTimeOffset From, DateTimeOffset To)
{
    public static WrappedSpan For(WrappedPeriod period, DateTimeZone zone, Instant now)
    {
        var today = now.InZone(zone).Date;
        var month = new LocalDate(today.Year, today.Month, 1);
        var year = new LocalDate(today.Year, 1, 1);
        return period switch
        {
            WrappedPeriod.ThisMonth => Make(month.ToString("MMMM yyyy", null), month, month.PlusMonths(1)),
            WrappedPeriod.LastMonth => Make(month.PlusMonths(-1).ToString("MMMM yyyy", null), month.PlusMonths(-1), month),
            WrappedPeriod.ThisYear => Make(today.Year.ToString(), year, year.PlusYears(1)),
            WrappedPeriod.LastYear => Make((today.Year - 1).ToString(), year.PlusYears(-1), year),
            _ => new("all time", DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
        };

        WrappedSpan Make(string label, LocalDate from, LocalDate to)
            => new(label, from.AtStartOfDayInZone(zone).ToInstant().ToDateTimeOffset(), to.AtStartOfDayInZone(zone).ToInstant().ToDateTimeOffset());
    }
}
