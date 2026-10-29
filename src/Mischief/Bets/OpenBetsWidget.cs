using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Notifications;
using THOBOTTO.Overview;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief.Bets;

// The bets not yet settled, with what's staked on each option: a line each, or a card each.
public sealed class OpenBetsWidget(IDbContextFactory<BotDbContext> dbFactory, PointsEngine points, TimeProvider time) : IWidgetKind
{
    public string Id => "bets";

    public string Name => "Open bets";

    public string Description => "bets not yet settled, with what's staked on each option";

    public IReadOnlyList<WidgetOption> Options =>
    [
        new("show", "Show", "cards", Choices: [new("lines", "A line each"), new("cards", "A card each")]),
        new("count", "How many", "6", "1 to 12"),
        new(WidgetOptions.Columns, "Cards abreast", "2", "1 to 3, in the columns look when the block has its row to itself"),
    ];

    public TimeSpan Refresh => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<WidgetBlock>> RenderAsync(ulong guildId, IReadOnlyDictionary<string, string> options)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var bets = await db.Bets.AsNoTracking().Where(b => b.GuildId == guildId && b.State == BetStates.Open).OrderBy(b => b.ClosesAt)
            .Take(WidgetOptions.Int(options, "count", 6, 1, 12)).ToListAsync();
        if (bets.Count == 0)
            return [];
        var ids = bets.Select(b => b.Id).ToList();
        var stakes = (await db.BetStakes.AsNoTracking().Where(s => ids.Contains(s.BetId)).ToListAsync()).ToLookup(s => s.BetId);
        var (rules, now) = (points.Rules(guildId), time.GetUtcNow());
        string When(Bet bet) => bet.ClosesAt > now ? $"closes <t:{bet.ClosesAt.ToUnixTimeSeconds()}:R>" : "closed, waiting for a result";
        string Link(Bet bet) => bet.MessageId is { } message ? $" · [the bet]({Notifier.Link(bet.GuildId, bet.ChannelId, message)})" : "";

        if (options.GetValueOrDefault("show") == "lines")
            return [new("Open bets", string.Join('\n', bets.Select(b => $"🎲 **{b.Question}** · {rules.Format(stakes[b.Id].Sum(s => s.Amount))} staked · {When(b)}{Link(b)}")))];
        return [.. bets.Select(b =>
        {
            var on = stakes[b.Id].ToLookup(s => s.Option);
            var lines = b.Options.Select((option, i) => (option, people: on[i].Select(s => s.UserId).Distinct().Count(), staked: on[i].Sum(s => s.Amount)))
                .Select(o => $"**{o.option}**: {rules.Format(o.staked)} from {o.people} {(o.people == 1 ? "person" : "people")}");
            return new WidgetBlock(b.Question, $"{string.Join('\n', lines)}\n-# {When(b)}{Link(b)}");
        })];
    }
}
