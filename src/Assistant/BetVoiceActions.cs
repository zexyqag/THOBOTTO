using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Points;

namespace THOBOTTO.Assistant;

// "Put twenty on yes": a stake on an open bet, asked about first (it spends points), and which bet when more than
// one has that option.
public sealed class BetVoiceActions(BetBook bets, PointsEngine points, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time) : IVoiceActions
{
    private const double Alike = 0.6;
    private const int MostAsked = 3;

    public IEnumerable<VoiceAction> Actions =>
    [
        new("bet", "Stake points on an option of an open bet.",
            new() { ["option"] = Schema.Text("The option, as said"), ["amount"] = Schema.Number("How many points"), ["bet"] = Schema.Text("Which bet, if said") }, ["option", "amount"],
            StakeAsync,
            ("put twenty on yes for the drake bet", """{"option":"yes","amount":20,"bet":"drake"}""")),
    ];

    private async Task<VoicePlan> StakeAsync(VoiceRequest request, System.Text.Json.JsonElement args)
    {
        var heard = request.Heard;
        if (Schema.String(args, "option") is not { } said || Schema.Int(args, "amount") is not ({ } amount and > 0))
            return VoicePlan.Done("Say how much, and on what.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var open = await db.Bets.AsNoTracking().Where(b => b.GuildId == heard.GuildId && b.State == BetStates.Open && b.ClosesAt > now).ToListAsync();
        var which = Schema.String(args, "bet");
        // Each open bet with an option like that, the best-fitting bets first.
        var fits = open
            .Select(b => (Bet: b, Option: b.Options.Select((o, i) => (Index: i, Score: Likeness.Of(said, o))).MaxBy(o => o.Score)))
            .Where(f => f.Option.Score >= Alike)
            .OrderByDescending(f => (which is null ? 0 : Likeness.Of(which, f.Bet.Question)) + f.Option.Score)
            .ToList();
        if (fits.Count == 0)
            return VoicePlan.Done(open.Count == 0 ? "There's no open bet." : $"No open bet has “{said}” as an option.");
        if (which is not null && fits.Count > 1 && Likeness.Of(which, fits[0].Bet.Question) >= Alike)
            fits = fits.Take(1).ToList();

        var what = points.Rules(heard.GuildId).Format(amount);
        VoiceChoice Stake((Bet Bet, (int Index, double Score) Option) fit, string label)
            => new(label, async () => await bets.StakeAsync(fit.Bet.Id, heard.UserId, fit.Option.Index, amount)
                ?? $"<@{heard.UserId}> put {what} on **{fit.Bet.Options[fit.Option.Index]}** in “{fit.Bet.Question}”.");
        return fits is [var only]
            ? VoicePlan.Ask($"Put {what} on **{only.Bet.Options[only.Option.Index]}** in “{only.Bet.Question}”?", [Stake(only, "Yes, bet")])
            : VoicePlan.Ask($"Put {what} on “{said}” in which bet?", [.. fits.Take(MostAsked).Select(f => Stake(f, $"{f.Bet.Options[f.Option.Index]} · {f.Bet.Question}"))]);
    }
}
