using NodaTime;

namespace THOBOTTO.Wrapped;

// The cards a server's or a member's Wrapped shows, in this order.
public enum WrappedCardKind
{
    Music,
    Discord,
}

// What a Wrapped is asked for: the server, the span in the zone it's read in, the bots whose activity is no one's,
// the member whose own it is (none for the server's), and how a channel is named.
public sealed record WrappedAsk(ulong GuildId, WrappedSpan Span, DateTimeZone Zone, IReadOnlySet<ulong> Bots, ulong? Member, Func<ulong, string> Channel);

// A line of a ranking: a thing, named by the feature, or a member, named by Wrapped (and only if they asked to be).
public sealed record WrappedLine(string Name, string? Detail = null, ulong? Member = null)
{
    public static WrappedLine Of(ulong member, string detail) => new("", detail, member);
}

public sealed record WrappedList(string Heading, IReadOnlyList<WrappedLine> Lines, int Most = WrappedText.Shown);

// A feature's part of a card: a phrase for the card's first line ("12 messages"; in a member's own, "sent 12
// messages"), its facts, and its rankings.
public sealed record WrappedPart(string? Intro, IReadOnlyList<(string Label, string Value)> Facts, IReadOnlyList<WrappedList> Lists);

// What a feature adds to Wrapped: its part of one card, for the server or for a member; null when it has nothing in
// the span.
public interface IWrappedSection
{
    WrappedCardKind Card { get; }

    // Its place on the card, lower first.
    int Order { get; }

    // The module whose data it shows: left out while that module is off.
    string ModuleId { get; }

    Task<WrappedPart?> PartAsync(WrappedAsk ask);
}

// The voices that played music here (personalities, and helpers wearing none), each with a Wrapped of its own.
public interface IWrappedVoices
{
    // "p:<personality>" or "h:<helper id>", with their names, most played first.
    Task<IReadOnlyList<(string Key, string Name)>> VoicesAsync(ulong guildId);

    // Null for a voice it doesn't know.
    Task<WrappedVoice?> VoiceAsync(WrappedAsk ask, string key);
}

// A voice's Wrapped: its name, colour and opening line, and its part (null when it played nothing in the span).
public sealed record WrappedVoice(string Name, int? Color, string Intro, WrappedPart? Part);

// How Wrapped words things, the same in every section.
public static class WrappedText
{
    // Members named in a ranking.
    public const int Shown = 5;

    // "1 entry", "2 entries", "2 plays".
    public static string Count(int n, string what)
        => $"{n} {(n == 1 ? what : what.Length > 1 && what[^1] == 'y' && !"aeiou".Contains(what[^2]) ? what[..^1] + "ies" : what + "s")}";

    public static string Hours(TimeSpan span) => span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" : $"{span.TotalMinutes:0} minutes";

    public static string Percent(double share) => $"{share * 100:0}%";

    public static string Busiest(IsoDayOfWeek day, int hour) => $"{day}s around {hour:00}:00";

    // Where a member stands among everyone, without naming anyone else; empty when they aren't ranked.
    public static string Rank<T>(IReadOnlyList<(ulong User, T Value)> ranked, ulong user)
        => ranked.Select((r, i) => (r.User, i)).FirstOrDefault(r => r.User == user) is { User: > 0 } found ? $"#{found.i + 1} of {ranked.Count}" : "";
}
