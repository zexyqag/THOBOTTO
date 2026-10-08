using System.Text.RegularExpressions;

using NodaTime;

namespace THOBOTTO.Events;

public sealed record When(Instant? At, string? Problem)
{
    public static When Fail(string problem) => new(null, problem);
}

// Reads times as people type them, in their time zone: "20:00", "8pm", "tomorrow 19:30",
// "fri 20:00", "next fri 8pm", "2026-10-10 20:00", "10.10 20:00", "10/10 20:00", "10 oct 20:00",
// "in 2h", "in 1d 30m". Dates are day first. Without a date, the next time the clock shows it.
public static partial class WhenParser
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] Weekdays = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    public static When Parse(string text, DateTimeZone zone, Instant now)
    {
        text = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

        if (Relative().Match(text) is { Success: true } relative)
            return RelativeTo(relative, now);

        if (TimeAtEnd().Match(text) is not { Success: true } timeMatch || ParseTime(timeMatch) is not { } time)
            return When.Fail("Give a time too, like `20:00` or `8pm`.");

        var datePart = TrailingAt().Replace(text[..timeMatch.Index], "").Trim();
        var today = now.InZone(zone).Date;
        var nowLocal = now.InZone(zone).LocalDateTime;

        LocalDate date;
        if (datePart is "" )
            date = today + time > nowLocal ? today : today.PlusDays(1);
        else if (datePart is "today" or "tonight")
            date = today;
        else if (datePart is "tomorrow" or "tmrw")
            date = today.PlusDays(1);
        else if (Weekday().Match(datePart) is { Success: true } weekday)
        {
            var target = (IsoDayOfWeek)(Array.FindIndex(Weekdays, d => weekday.Groups["day"].Value.StartsWith(d)) + 1);
            var days = ((int)target - (int)today.DayOfWeek + 7) % 7;
            if (weekday.Groups["next"].Success ? days == 0 : days == 0 && today + time <= nowLocal)
                days = 7;
            date = today.PlusDays(days);
        }
        else if (ParseDate(datePart, today) is { } explicitDate)
            date = explicitDate;
        else
            return When.Fail($"I don't understand the date `{datePart}`. Try `fri`, `tomorrow`, `2026-10-10` or `10.10`.");

        var at = zone.AtLeniently(date + time).ToInstant();
        return at > now ? new(at, null) : When.Fail("That's in the past.");
    }

    private static When RelativeTo(Match match, Instant now)
    {
        var total = Duration.Zero;
        foreach (Capture part in match.Groups["part"].Captures)
        {
            var piece = PartPattern().Match(part.Value);
            var amount = int.Parse(piece.Groups[1].Value);
            total += piece.Groups[2].Value[0] switch
            {
                'd' => Duration.FromDays(amount),
                'h' => Duration.FromHours(amount),
                _ => Duration.FromMinutes(amount),
            };
        }
        return total > Duration.Zero ? new(now + total, null) : When.Fail("That's now.");
    }

    // A clock time on its own: "20:00", "8pm".
    public static LocalTime? ParseClock(string text)
        => TimeAtEnd().Match(text.Trim().ToLowerInvariant()) is { Success: true, Index: 0 } match ? ParseTime(match) : null;

    private static LocalTime? ParseTime(Match match)
    {
        var hour = int.Parse(match.Groups["h"].Value);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value) : 0;
        switch (match.Groups["ampm"].Value)
        {
            case "am" when hour is >= 1 and <= 12:
                hour %= 12;
                break;
            case "pm" when hour is >= 1 and <= 12:
                hour = hour % 12 + 12;
                break;
            case "am" or "pm":
                return null;
        }
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? new LocalTime(hour, minute) : null;
    }

    private static LocalDate? ParseDate(string text, LocalDate today)
    {
        try
        {
            if (IsoDate().Match(text) is { Success: true } iso)
                return new LocalDate(int.Parse(iso.Groups["y"].Value), int.Parse(iso.Groups["mo"].Value), int.Parse(iso.Groups["d"].Value));

            if (DayMonth().Match(text) is { Success: true } dm)
                return Year(today, int.Parse(dm.Groups["d"].Value), int.Parse(dm.Groups["mo"].Value), dm.Groups["y"]);

            if (NamedMonth().Match(text) is { Success: true } named)
            {
                var month = Array.FindIndex(Months, m => named.Groups["mon"].Value.StartsWith(m)) + 1;
                return month == 0 ? null : Year(today, int.Parse(named.Groups["d"].Value), month, named.Groups["y"]);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // An impossible date such as 31.02.
        }
        return null;
    }

    // Without a year: this year, or next year if that day has passed.
    private static LocalDate Year(LocalDate today, int day, int month, Group year)
    {
        if (year.Success)
            return new LocalDate(year.Value.Length == 2 ? 2000 + int.Parse(year.Value) : int.Parse(year.Value), month, day);
        var date = new LocalDate(today.Year, month, day);
        return date < today ? date.PlusYears(1) : date;
    }

    [GeneratedRegex(@"\s*(?:\bat|@|,)?\s*$")]
    private static partial Regex TrailingAt();

    [GeneratedRegex(@"^in (?<part>\d+ ?(?:d|days?|h|hours?|hrs?|m|mins?|minutes?) ?)+$")]
    private static partial Regex Relative();

    [GeneratedRegex(@"^(\d+) ?([a-z])")]
    private static partial Regex PartPattern();

    [GeneratedRegex(@"(?<h>\d{1,2})(?::(?<m>\d{2}))? ?(?<ampm>am|pm)?$")]
    private static partial Regex TimeAtEnd();

    [GeneratedRegex(@"^(?<next>next )?(?<day>mon|tue|wed|thu|fri|sat|sun)[a-z]*$")]
    private static partial Regex Weekday();

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{1,2})-(?<d>\d{1,2})$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(?<d>\d{1,2})[./](?<mo>\d{1,2})(?:[./](?<y>\d{2}|\d{4}))?$")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"^(?<d>\d{1,2}) (?<mon>[a-z]{3,9})(?: (?<y>\d{4}))?$")]
    private static partial Regex NamedMonth();
}
