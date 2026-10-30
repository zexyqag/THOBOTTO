using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Wrapped;

using static THOBOTTO.Wrapped.WrappedText;

namespace THOBOTTO.Events;

// Events in Wrapped: the ones held in the span that people said they'd join, and who joined most.
public sealed class EventsWrapped(IDbContextFactory<BotDbContext> dbFactory) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 30;

    public string ModuleId => EventBoard.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to, member) = (ask.Span.From, ask.Span.To, ask.Member);
        var going = await db.EventRsvps.AsNoTracking()
            .Where(r => r.Status == RsvpStatuses.In && (member == null || r.UserId == member))
            .Join(db.Events.Where(e => e.GuildId == ask.GuildId && e.State != EventStates.Cancelled && e.StartsAt >= from && e.StartsAt < to), r => r.EventId, e => e.Id, (r, e) => new { r.UserId, e.Id })
            .ToListAsync();
        if (going.Count == 0)
            return null;
        var held = going.Select(g => g.Id).Distinct().Count().ToString();
        if (member is not null)
            return new(null, [("Events joined", held)], []);
        var regulars = going.GroupBy(g => g.UserId).Select(g => (User: g.Key, Count: g.Count())).OrderByDescending(r => r.Count);
        return new(null, [("Events held", held)], [new("Event regulars", [.. regulars.Select(r => WrappedLine.Of(r.User, Count(r.Count, "event")))])]);
    }
}
