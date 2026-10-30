using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Points;

// Points in Wrapped: what was earned, and kudos given and received.
public sealed class PointsWrapped(IDbContextFactory<BotDbContext> dbFactory, SettingsStore settings) : IWrappedSection
{
    public WrappedCardKind Card => WrappedCardKind.Discord;

    public int Order => 40;

    public string ModuleId => PointsEngine.ModuleId;

    public async Task<WrappedPart?> PartAsync(WrappedAsk ask)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (from, to, member) = (ask.Span.From, ask.Span.To, ask.Member);
        var entries = await db.PointEntries.AsNoTracking()
            .Where(p => p.GuildId == ask.GuildId && p.CreatedAt >= from && p.CreatedAt < to && (member == null || p.UserId == member || (p.Kind == PointEntryKinds.Kudos && p.ActorId == member)))
            .ToListAsync();
        var rules = await settings.GetAsync<PointRules>(ask.GuildId, PointsEngine.ModuleId);
        var earned = entries.Where(p => p.Earned && (member == null || p.UserId == member)).ToList();
        // A kudos is two entries: the giver's minus, the receiver's plus (with the giver as actor).
        var kudos = entries.Where(p => p.Kind == PointEntryKinds.Kudos && p.Amount > 0 && p.ActorId is not null).ToList();
        var total = earned.Sum(p => p.Amount);

        List<(string, string)> facts = [];
        if (member is { } me)
        {
            var (got, gave) = (kudos.Count(k => k.UserId == me), kudos.Count(k => k.ActorId == me));
            if (got + gave > 0)
                facts.Add(("Kudos", $"{got} received, {gave} given"));
            if (total > 0)
                facts.Add(("Earned", rules.Format(total)));
            return facts.Count == 0 ? null : new(null, facts, []);
        }
        if (kudos.Count > 0)
            facts.Add(("Kudos given", kudos.Count.ToString()));
        if (total > 0)
            facts.Add(("Earned", rules.Format(total)));
        if (facts.Count == 0)
            return null;
        var received = kudos.GroupBy(k => k.UserId).Select(g => (User: g.Key, Count: g.Count())).OrderByDescending(x => x.Count);
        var earners = earned.GroupBy(p => p.UserId).Select(g => (User: g.Key, Amount: g.Sum(p => p.Amount))).OrderByDescending(x => x.Amount);
        return new(null, facts,
            [new("Most kudos", [.. received.Select(x => WrappedLine.Of(x.User, $"{x.Count} kudos"))]), new("Top earners", [.. earners.Select(x => WrappedLine.Of(x.User, rules.Format(x.Amount)))])]);
    }
}
