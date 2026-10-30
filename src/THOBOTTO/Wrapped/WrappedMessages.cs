using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using THOBOTTO.Archive;
using THOBOTTO.Data;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Wrapped;

// Messages in Wrapped, from the archive: how many, where, which emoji, and the busiest time in the zone it's read in.
// Bots' are left out: Discord marks them in the message as archived (ours are also known by id, for messages archived
// without it).
public sealed partial class WrappedMessages(IDbContextFactory<BotDbContext> dbFactory) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 10;

    public string ModuleId => Archiver.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to) = (ask.Span.From, ask.Span.To);
        var bots = ask.Bots.ToArray();
        var everyone = db.ArchivedMessages.AsNoTracking().Where(m => m.GuildId == ask.GuildId && m.CreatedAt >= from && m.CreatedAt < to
            && !bots.Contains(m.AuthorId) && (m.Raw == null || !EF.Functions.JsonContains(m.Raw, """{"author":{"bot":true}}""")));
        var talkers = Ranked(await everyone.GroupBy(m => m.AuthorId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count));
        var messages = ask.Member is { } author ? everyone.Where(m => m.AuthorId == author) : everyone;
        var count = ask.Member is { } member ? talkers.FirstOrDefault(t => t.Id == member).Count : talkers.Sum(t => t.Count);
        if (count == 0)
            return null;

        // Counted where they are, per channel and hour of the zone; only the ones with an emoji are read, for the counting.
        var channels = Ranked(await messages.GroupBy(m => m.ChannelId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count));
        var zoneId = ask.Zone.Id;
        var hours = await messages.Select(m => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(m.CreatedAt.UtcDateTime, zoneId))
            .GroupBy(t => new { t.DayOfWeek, t.Hour }).Select(g => new { g.Key.DayOfWeek, g.Key.Hour, Count = g.Count() })
            .ToListAsync();
        var emoji = new Dictionary<string, int>();
        await foreach (var content in messages.Where(m => m.Content != null && Regex.IsMatch(m.Content, EmojiPattern)).Select(m => m.Content!).AsAsyncEnumerable())
        {
            foreach (var e in EmojiIn(content))
                emoji[e] = emoji.GetValueOrDefault(e) + 1;
        }

        List<(string, string)> facts = [("Messages", count.ToString())];
        if (hours.MaxBy(h => h.Count) is { } busiest)
            facts.Add(("Busiest time", Busiest((IsoDayOfWeek)(busiest.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)busiest.DayOfWeek), busiest.Hour)));
        List<WrappedLine> where = [.. channels.Take(Shown).Select(c => new WrappedLine(ask.Channel(c.Id), Count(c.Count, "message")))];
        List<WrappedLine> used = [.. emoji.OrderByDescending(e => e.Value).Take(Shown).Select(e => new WrappedLine(e.Key, $"{e.Value}×"))];
        if (ask.Member is { } me)
        {
            facts.Add(("Chattiest rank", Rank(talkers, me)));
            return new($"sent {Count(count, "message")}", facts, [new("Your channels", where), new("Your emoji", used)]);
        }
        return new(Count(count, "message"), facts,
            [new("Busiest channels", where), new("Top emoji", used), new("Chattiest", [.. talkers.Select(t => WrappedLine.Of(t.Id, Count(t.Count, "message")))])]);
    }

    // What EmojiIn finds, as Postgres reads it.
    private const string EmojiPattern = "<a?:\\w+:\\d+>|[☀-➿\U0001F300-\U0001FAFF]";

    // Server emoji as written (<:name:id>), and pictographs, each counted once per message.
    public static IEnumerable<string> EmojiIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var found = new HashSet<string>();
        foreach (Match m in CustomEmoji().Matches(text))
            found.Add(m.Value);
        foreach (var rune in CustomEmoji().Replace(text, "").EnumerateRunes())
        {
            if (rune.Value is >= 0x1F300 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF)
                found.Add(rune.ToString());
        }
        return found;
    }

    private static List<(ulong Id, int Count)> Ranked(Dictionary<ulong, int> counts) => [.. counts.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value))];

    [GeneratedRegex("<a?:\\w+:\\d+>")]
    private static partial Regex CustomEmoji();
}
