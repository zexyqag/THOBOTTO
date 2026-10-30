using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Points;

namespace THOBOTTO.Wrapped;

public sealed record ActivitySummary(
    int Messages,
    IReadOnlyList<(ulong User, int Count)> Talkers,
    IReadOnlyList<(ulong Channel, int Count)> Channels,
    IReadOnlyList<(string Emoji, int Count)> Emoji,
    int? BusiestHour,
    IsoDayOfWeek? BusiestDay,
    TimeSpan Voice,
    IReadOnlyList<(ulong User, TimeSpan Time)> InVoice,
    IReadOnlyList<(ulong Channel, TimeSpan Time)> VoiceChannels,
    TimeSpan Talk,
    IReadOnlyList<(ulong User, TimeSpan Time)> Talkative,
    int Events,
    IReadOnlyList<(ulong User, int Count)> EventGoers,
    double Earned,
    IReadOnlyList<(ulong User, double Amount)> Earners,
    int Kudos,
    IReadOnlyList<(ulong User, int Count)> KudosGiven,
    IReadOnlyList<(ulong User, int Count)> KudosReceived,
    int Quotes,
    IReadOnlyList<(ulong User, int Count)> Quoted,
    int Fame,
    IReadOnlyList<(ulong User, int Count)> Famous)
{
    public bool Empty => Messages == 0 && Voice == TimeSpan.Zero && Events == 0 && Kudos == 0 && Quotes == 0 && Fame == 0 && Earned == 0;
}

// The numbers behind a Discord Wrapped: messages (from the archive, where it's on), voice and talk time, events,
// points and kudos, quotes and the hall of fame. For the server, or one member.
public sealed partial class DiscordStats(IDbContextFactory<BotDbContext> dbFactory)
{
    private const int Top = 5;

    public async Task<ActivitySummary> ForAsync(ulong guildId, ulong? userId, WrappedSpan span, DateTimeZone zone, IReadOnlySet<ulong> bots)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to) = (span.From, span.To);

        // Messages: counted where they are, per author, channel and hour of the server's zone; only the ones with an
        // emoji are read, for the counting. Bots' are left out: Discord marks them in the message as archived (ours
        // are also known by id, for messages archived without it).
        var botIds = bots.ToArray();
        var zoneId = zone.Id;
        var messages = db.ArchivedMessages.AsNoTracking().Where(m => m.GuildId == guildId && m.CreatedAt >= from && m.CreatedAt < to
            && !botIds.Contains(m.AuthorId) && (m.Raw == null || !EF.Functions.JsonContains(m.Raw, """{"author":{"bot":true}}""")));
        if (userId is { } author)
            messages = messages.Where(m => m.AuthorId == author);
        var talkers = await messages.GroupBy(m => m.AuthorId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var channels = await messages.GroupBy(m => m.ChannelId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var hours = await messages.Select(m => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(m.CreatedAt.UtcDateTime, zoneId))
            .GroupBy(t => new { t.DayOfWeek, t.Hour }).Select(g => new { g.Key.DayOfWeek, g.Key.Hour, Count = g.Count() })
            .ToListAsync();
        var emoji = new Dictionary<string, int>();
        await foreach (var content in messages.Where(m => m.Content != null && Regex.IsMatch(m.Content, EmojiPattern)).Select(m => m.Content!).AsAsyncEnumerable())
        {
            foreach (var e in EmojiIn(content))
                emoji[e] = emoji.GetValueOrDefault(e) + 1;
        }

        // Voice: each session's part inside the period.
        var sessions = await db.VoiceSessions.AsNoTracking()
            .Where(s => s.GuildId == guildId && s.JoinedAt < to && (s.LeftAt ?? s.SeenAt) > from && (userId == null || s.UserId == userId))
            .ToListAsync();
        var spans = sessions.Select(s => (s.UserId, s.ChannelId, Time: Min(s.LeftAt ?? s.SeenAt, to) - Max(s.JoinedAt, from))).Where(s => s.Time > TimeSpan.Zero).ToList();

        // Talk time is kept per UTC day: the days the period touches.
        var (fromDay, toDay) = (DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime.AddTicks(-1)));
        var talk = await db.TalkTimes.AsNoTracking()
            .Where(t => t.GuildId == guildId && t.Day >= fromDay && t.Day <= toDay && (userId == null || t.UserId == userId))
            .GroupBy(t => t.UserId).Select(g => new { User = g.Key, Ms = g.Sum(t => t.Ms) })
            .ToListAsync();

        // Events held in the period that people said they'd join.
        var going = await db.EventRsvps.AsNoTracking()
            .Where(r => r.Status == RsvpStatuses.In && (userId == null || r.UserId == userId))
            .Join(db.Events.Where(e => e.GuildId == guildId && e.State != EventStates.Cancelled && e.StartsAt >= from && e.StartsAt < to), r => r.EventId, e => e.Id, (r, e) => new { r.UserId, e.Id })
            .ToListAsync();

        var entries = await db.PointEntries.AsNoTracking()
            .Where(p => p.GuildId == guildId && p.CreatedAt >= from && p.CreatedAt < to && (userId == null || p.UserId == userId || (p.Kind == PointEntryKinds.Kudos && p.ActorId == userId)))
            .ToListAsync();
        var earned = entries.Where(p => p.Amount > 0 && PointEntryKinds.Earned.Contains(p.Kind) && (userId == null || p.UserId == userId)).ToList();
        // A kudos is two entries: the giver's minus, the receiver's plus (with the giver as actor).
        var kudos = entries.Where(p => p.Kind == PointEntryKinds.Kudos && p.Amount > 0 && p.ActorId is not null).ToList();

        var quotes = await db.Quotes.AsNoTracking().Where(q => q.GuildId == guildId && q.SaidAt >= from && q.SaidAt < to)
            .Select(q => new { q.Id, Speakers = q.Lines.Where(l => l.SpeakerId != null).Select(l => l.SpeakerId!.Value).Distinct().ToList() })
            .ToListAsync();
        if (userId is { } quotedUser)
            quotes = quotes.Where(q => q.Speakers.Contains(quotedUser)).ToList();

        var fame = await db.FameEntries.AsNoTracking().Where(f => f.GuildId == guildId && f.InductedAt >= from && f.InductedAt < to && (userId == null || f.AuthorId == userId)).ToListAsync();

        var busiest = hours.MaxBy(h => h.Count);
        return new(
            talkers.Values.Sum(),
            Ranked(talkers),
            Ranked(channels),
            emoji.OrderByDescending(e => e.Value).Take(Top).Select(e => (e.Key, e.Value)).ToList(),
            busiest?.Hour,
            busiest is null ? null : (IsoDayOfWeek)(busiest.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)busiest.DayOfWeek),
            TimeSpan.FromTicks(spans.Sum(s => s.Time.Ticks)),
            spans.GroupBy(s => s.UserId).Select(g => (g.Key, TimeSpan.FromTicks(g.Sum(s => s.Time.Ticks)))).OrderByDescending(x => x.Item2).ToList(),
            spans.GroupBy(s => s.ChannelId).Select(g => (g.Key, TimeSpan.FromTicks(g.Sum(s => s.Time.Ticks)))).OrderByDescending(x => x.Item2).Take(Top).ToList(),
            TimeSpan.FromMilliseconds(talk.Sum(t => t.Ms)),
            talk.Where(t => !bots.Contains(t.User)).OrderByDescending(t => t.Ms).Select(t => (t.User, TimeSpan.FromMilliseconds(t.Ms))).ToList(),
            going.Select(g => g.Id).Distinct().Count(),
            Ranked(going.GroupBy(g => g.UserId).ToDictionary(g => g.Key, g => g.Count())),
            earned.Sum(p => p.Amount),
            earned.GroupBy(p => p.UserId).Select(g => (g.Key, g.Sum(p => p.Amount))).OrderByDescending(x => x.Item2).ToList(),
            userId is { } me ? kudos.Count(k => k.UserId == me || k.ActorId == me) : kudos.Count,
            Ranked(kudos.GroupBy(k => k.ActorId!.Value).ToDictionary(g => g.Key, g => g.Count())),
            Ranked(kudos.GroupBy(k => k.UserId).ToDictionary(g => g.Key, g => g.Count())),
            quotes.Count,
            Ranked(quotes.SelectMany(q => q.Speakers).GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count())),
            fame.Count,
            Ranked(fame.GroupBy(f => f.AuthorId).ToDictionary(g => g.Key, g => g.Count())));
    }

    // What EmojiIn finds, as Postgres reads it.
    private const string EmojiPattern = "<a?:\\w+:\\d+>|[\u2600-\u27BF\U0001F300-\U0001FAFF]";

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

    private static List<(ulong, int)> Ranked(Dictionary<ulong, int> counts) => counts.OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value)).ToList();

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    [GeneratedRegex("<a?:\\w+:\\d+>")]
    private static partial Regex CustomEmoji();
}
