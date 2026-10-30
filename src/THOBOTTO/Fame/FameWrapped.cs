using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Wrapped;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Fame;

// The hall of fame in Wrapped: the entries inducted in the span, and whose.
public sealed class FameWrapped(IDbContextFactory<BotDbContext> dbFactory) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 60;

    public string ModuleId => HallOfFame.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to, member) = (ask.Span.From, ask.Span.To, ask.Member);
        var authors = await db.FameEntries.AsNoTracking()
            .Where(f => f.GuildId == ask.GuildId && f.InductedAt >= from && f.InductedAt < to && (member == null || f.AuthorId == member))
            .Select(f => f.AuthorId)
            .ToListAsync();
        if (authors.Count == 0)
            return null;
        if (member is not null)
            return new(null, [("Hall of fame", Count(authors.Count, "entry"))], []);
        var famous = authors.GroupBy(a => a).Select(g => (User: g.Key, Count: g.Count())).OrderByDescending(x => x.Count);
        return new(null, [("Hall of fame", authors.Count.ToString())], [new("Hall of famers", [.. famous.Select(x => WrappedLine.Of(x.User, Count(x.Count, "entry")))])]);
    }
}
