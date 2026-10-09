using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Points;

namespace THOBOTTO.Stats;

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

// The numbers behind a Discord Wrapped: messages (from the archive, where it's on), voice time, events,
// points and kudos, quotes and the hall of fame. For the server, or one member.
public sealed partial class ActivityStats(IDbContextFactory<BotDbContext> dbFactory)
{
    private const int Top = 5;

    // Earned, as opposed to points moved around: activity, the hall of fame, royalties, kudos received.
    private static readonly string[] Earnings = [PointEntryKinds.Activity, PointEntryKinds.Fame, PointEntryKinds.Royalty, PointEntryKinds.Kudos];

    public async Task<ActivitySummary> ForAsync(ulong guildId, ulong? userId, WrappedSpan span, DateTimeZone zone, IReadOnlySet<ulong> bots)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to) = (span.From, span.To);

        // Messages: counted per author and channel, and the emoji in them. Bots' are left out: Discord marks them
        // in the message as archived (ours are also known by id, for messages archived without it).
        var messages = db.ArchivedMessages.AsNoTracking().Where(m => m.GuildId == guildId && m.CreatedAt >= from && m.CreatedAt < to
            && (m.Raw == null || !EF.Functions.JsonContains(m.Raw, """{"author":{"bot":true}}""")));
        if (userId is { } author)
            messages = messages.Where(m => m.AuthorId == author);
        var talkers = new Dictionary<ulong, int>();
        var channels = new Dictionary<ulong, int>();
        var emoji = new Dictionary<string, int>();
        var hours = new Dictionary<(IsoDayOfWeek Day, int Hour), int>();
        await foreach (var m in messages.Select(m => new { m.AuthorId, m.ChannelId, m.Content, m.CreatedAt }).AsAsyncEnumerable())
        {
            if (bots.Contains(m.AuthorId))
                continue;
            talkers[m.AuthorId] = talkers.GetValueOrDefault(m.AuthorId) + 1;
            channels[m.ChannelId] = channels.GetValueOrDefault(m.ChannelId) + 1;
            var local = Instant.FromDateTimeOffset(m.CreatedAt).InZone(zone);
            hours[(local.DayOfWeek, local.Hour)] = hours.GetValueOrDefault((local.DayOfWeek, local.Hour)) + 1;
            foreach (var e in EmojiIn(m.Content))
                emoji[e] = emoji.GetValueOrDefault(e) + 1;
        }

        // Voice: each session's part inside the period.
        var sessions = await db.VoiceSessions.AsNoTracking()
            .Where(s => s.GuildId == guildId && s.JoinedAt < to && (s.LeftAt ?? s.SeenAt) > from && (userId == null || s.UserId == userId))
            .ToListAsync();
        var spans = sessions.Select(s => (s.UserId, s.ChannelId, Time: Min(s.LeftAt ?? s.SeenAt, to) - Max(s.JoinedAt, from))).Where(s => s.Time > TimeSpan.Zero).ToList();

        // Events held in the period that people said they'd join.
        var going = await db.EventRsvps.AsNoTracking()
            .Where(r => r.Status == RsvpStatuses.In && (userId == null || r.UserId == userId))
            .Join(db.Events.Where(e => e.GuildId == guildId && e.State != EventStates.Cancelled && e.StartsAt >= from && e.StartsAt < to), r => r.EventId, e => e.Id, (r, e) => new { r.UserId, e.Id })
            .ToListAsync();

        var entries = await db.PointEntries.AsNoTracking()
            .Where(p => p.GuildId == guildId && p.CreatedAt >= from && p.CreatedAt < to && (userId == null || p.UserId == userId || (p.Kind == PointEntryKinds.Kudos && p.ActorId == userId)))
            .ToListAsync();
        var earned = entries.Where(p => p.Amount > 0 && Earnings.Contains(p.Kind) && (userId == null || p.UserId == userId)).ToList();
        // A kudos is two entries: the giver's minus, the receiver's plus (with the giver as actor).
        var kudos = entries.Where(p => p.Kind == PointEntryKinds.Kudos && p.Amount > 0 && p.ActorId is not null).ToList();

        var quotes = await db.Quotes.AsNoTracking().Where(q => q.GuildId == guildId && q.SaidAt >= from && q.SaidAt < to)
            .Select(q => new { q.Id, Speakers = q.Lines.Where(l => l.SpeakerId != null).Select(l => l.SpeakerId!.Value).Distinct().ToList() })
            .ToListAsync();
        if (userId is { } quotedUser)
            quotes = quotes.Where(q => q.Speakers.Contains(quotedUser)).ToList();

        var fame = await db.FameEntries.AsNoTracking().Where(f => f.GuildId == guildId && f.InductedAt >= from && f.InductedAt < to && (userId == null || f.AuthorId == userId)).ToListAsync();

        var busiest = hours.Count == 0 ? default : hours.MaxBy(h => h.Value).Key;
        return new(
            talkers.Values.Sum(),
            Ranked(talkers),
            Ranked(channels),
            emoji.OrderByDescending(e => e.Value).Take(Top).Select(e => (e.Key, e.Value)).ToList(),
            hours.Count == 0 ? null : busiest.Hour,
            hours.Count == 0 ? null : busiest.Day,
            TimeSpan.FromTicks(spans.Sum(s => s.Time.Ticks)),
            spans.GroupBy(s => s.UserId).Select(g => (g.Key, TimeSpan.FromTicks(g.Sum(s => s.Time.Ticks)))).OrderByDescending(x => x.Item2).ToList(),
            spans.GroupBy(s => s.ChannelId).Select(g => (g.Key, TimeSpan.FromTicks(g.Sum(s => s.Time.Ticks)))).OrderByDescending(x => x.Item2).Take(Top).ToList(),
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
