using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Wrapped;

// Time in voice in Wrapped, from the sessions Wrapped keeps, and time talking, from what the helpers heard.
public sealed class WrappedVoiceTime(IDbContextFactory<BotDbContext> dbFactory) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 20;

    public string ModuleId => WrappedPoster.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to) = (ask.Span.From, ask.Span.To);

        // Each session's part inside the span.
        var sessions = await db.VoiceSessions.AsNoTracking()
            .Where(s => s.GuildId == ask.GuildId && s.JoinedAt < to && (s.LeftAt ?? s.SeenAt) > from)
            .ToListAsync();
        var spans = sessions.Select(s => (s.UserId, s.ChannelId, Time: Min(s.LeftAt ?? s.SeenAt, to) - Max(s.JoinedAt, from))).Where(s => s.Time > TimeSpan.Zero).ToList();
        List<(ulong User, TimeSpan Time)> inVoice = [.. spans.GroupBy(s => s.UserId).Select(g => (g.Key, Sum(g.Select(s => s.Time)))).OrderByDescending(x => x.Item2)];

        // Talk time is kept per UTC day: the days the span touches.
        var (fromDay, toDay) = (DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime.AddTicks(-1)));
        var talk = await db.TalkTimes.AsNoTracking()
            .Where(t => t.GuildId == ask.GuildId && t.Day >= fromDay && t.Day <= toDay)
            .GroupBy(t => t.UserId).Select(g => new { User = g.Key, Ms = g.Sum(t => t.Ms) })
            .ToListAsync();
        List<(ulong User, TimeSpan Time)> talkative = [.. talk.Where(t => !ask.Bots.Contains(t.User)).OrderByDescending(t => t.Ms).Select(t => (t.User, TimeSpan.FromMilliseconds(t.Ms)))];

        if (ask.Member is { } me)
        {
            var (voice, talked) = (inVoice.FirstOrDefault(v => v.User == me).Time, TimeSpan.FromMilliseconds(talk.FirstOrDefault(t => t.User == me)?.Ms ?? 0));
            if (voice == TimeSpan.Zero && talked == TimeSpan.Zero)
                return null;
            List<(string, string)> mine = [("Hours in voice", Hours(voice))];
            if (talked > TimeSpan.Zero)
                mine.Add(("Talking", Hours(talked)));
            mine.AddRange(new[] { ("Voice rank", Rank(inVoice, me)), ("Talk rank", Rank(talkative, me)) }.Where(r => r.Item2.Length > 0));
            var channels = spans.Where(s => s.UserId == me).GroupBy(s => s.ChannelId).Select(g => (Channel: g.Key, Time: Sum(g.Select(s => s.Time)))).OrderByDescending(c => c.Time).Take(Shown);
            return new($"spent {Hours(voice)} in voice", mine, [new("Your voice channels", [.. channels.Select(c => new WrappedLine(ask.Channel(c.Channel), Hours(c.Time)))])]);
        }

        var (total, talking) = (Sum(spans.Select(s => s.Time)), TimeSpan.FromMilliseconds(talk.Sum(t => t.Ms)));
        if (total == TimeSpan.Zero && talking == TimeSpan.Zero)
            return null;
        List<(string, string)> facts = [("Hours in voice", Hours(total))];
        if (talking > TimeSpan.Zero)
            facts.Add(("Talking", Hours(talking)));
        return new($"{Hours(total)} in voice", facts,
            [new("Most time in voice", [.. inVoice.Select(v => WrappedLine.Of(v.User, Hours(v.Time)))]), new("Most talkative", [.. talkative.Select(t => WrappedLine.Of(t.User, Hours(t.Time)))])]);
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> times) => TimeSpan.FromTicks(times.Sum(t => t.Ticks));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
