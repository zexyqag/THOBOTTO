using System.Text.RegularExpressions;

namespace THOBOTTO.Moderation;

// "30m", "1h30m", "2d", "1w 3d": what moderators type for how long something lasts.
public static partial class Durations
{
    public static TimeSpan? Parse(string text)
    {
        var compact = text.Replace(" ", "").ToLowerInvariant();
        var parts = Part().Matches(compact);
        if (compact.Length == 0 || parts.Sum(m => m.Length) != compact.Length)
            return null;

        var total = TimeSpan.Zero;
        foreach (Match part in parts)
        {
            var n = int.Parse(part.Groups[1].Value);
            total += part.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                _ => TimeSpan.FromDays(7 * n),
            };
        }
        return total > TimeSpan.Zero ? total : null;
    }

    public static string Format(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Days >= 7)
            parts.Add($"{span.Days / 7}w");
        if (span.Days % 7 > 0)
            parts.Add($"{span.Days % 7}d");
        if (span.Hours > 0)
            parts.Add($"{span.Hours}h");
        if (span.Minutes > 0)
            parts.Add($"{span.Minutes}m");
        return parts.Count == 0 ? $"{(int)span.TotalSeconds}s" : string.Join(' ', parts);
    }

    [GeneratedRegex(@"(\d{1,5})(w|d|h|m|s)")]
    private static partial Regex Part();
}
