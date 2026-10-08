using NodaTime;

namespace THOBOTTO.Events;

public static class Recurrence
{
    // "fri", "mon, thu", "daily", "weekdays", "weekends". Null if a part isn't a day.
    public static int[]? ParseDays(string text)
    {
        var parts = text.ToLowerInvariant().Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var days = new SortedSet<int>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case "daily" or "everyday":
                    days.UnionWith([1, 2, 3, 4, 5, 6, 7]);
                    break;
                case "weekdays":
                    days.UnionWith([1, 2, 3, 4, 5]);
                    break;
                case "weekends":
                    days.UnionWith([6, 7]);
                    break;
                default:
                    var index = Array.FindIndex(["mon", "tue", "wed", "thu", "fri", "sat", "sun"], d => part.StartsWith(d));
                    if (index < 0)
                        return null;
                    days.Add(index + 1);
                    break;
            }
        }
        return days.Count == 0 ? null : [.. days];
    }

    // Occurrences starting after now and no later than now + window, in the series' zone.
    public static IEnumerable<Instant> Upcoming(int[] days, LocalTime time, DateTimeZone zone, Instant now, Duration window)
    {
        var today = now.InZone(zone).Date;
        for (var date = today; ; date = date.PlusDays(1))
        {
            var at = zone.AtLeniently(date + time).ToInstant();
            if (at > now + window)
                yield break;
            if (at > now && days.Contains((int)date.DayOfWeek))
                yield return at;
        }
    }

    public static string Describe(int[] days)
        => days.Length == 7 ? "every day"
            : days.SequenceEqual([1, 2, 3, 4, 5]) ? "weekdays"
            : days.SequenceEqual([6, 7]) ? "weekends"
            : "every " + string.Join(", ", days.Select(d => ((IsoDayOfWeek)d).ToString()));
}
