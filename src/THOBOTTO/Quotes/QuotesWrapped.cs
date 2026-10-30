using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Wrapped;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Quotes;

// Quotes in Wrapped: the ones said in the span, and who's quoted most.
public sealed class QuotesWrapped(IDbContextFactory<BotDbContext> dbFactory) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 50;

    public string ModuleId => QuoteCommands.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to) = (ask.Span.From, ask.Span.To);
        var speakers = await db.Quotes.AsNoTracking().Where(q => q.GuildId == ask.GuildId && q.SaidAt >= from && q.SaidAt < to)
            .Select(q => q.Lines.Where(l => l.SpeakerId != null).Select(l => l.SpeakerId!.Value).Distinct().ToList())
            .ToListAsync();
        if (ask.Member is { } me)
        {
            var quoted = speakers.Count(s => s.Contains(me));
            return quoted == 0 ? null : new(null, [("Quoted", Count(quoted, "time"))], []);
        }
        if (speakers.Count == 0)
            return null;
        var most = speakers.SelectMany(s => s).GroupBy(s => s).Select(g => (User: g.Key, Count: g.Count())).OrderByDescending(x => x.Count);
        return new(null, [("Quotes saved", speakers.Count.ToString())], [new("Most quoted", [.. most.Select(x => WrappedLine.Of(x.User, Count(x.Count, "quote")))])]);
    }
}
