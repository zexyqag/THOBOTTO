using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief.Bets;

// Every change to a bet goes through here, one at a time, so stakes, resolutions and reverts
// never interleave. A sweep marks bets closed on their message and cancels forgotten ones.
public sealed class BetBook(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    PointsEngine points,
    TimeProvider time,
    ILogger<BetBook> logger) : BackgroundService
{
    public const int MaxOptions = 5;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Bet> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string question, string[] options, TimeSpan duration)
    {
        var rules = await settings.GetAsync<MischiefRules>(guildId, MischiefCommands.ModuleId);
        var now = time.GetUtcNow();
        var bet = new Bet
        {
            GuildId = guildId,
            ChannelId = channelId,
            CreatorId = creatorId,
            Question = question,
            Options = options,
            CreatedAt = now,
            ClosesAt = now + duration,
            CreatorCutPercent = rules.BetCreatorCutPercent,
            HouseCutPercent = rules.BetHouseCutPercent,
            CreatorCanBet = rules.BetCreatorCanBet,
        };

        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Bets.Add(bet);
            await db.SaveChangesAsync();
            return bet;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Returns an error to show the member, or null on success.
    public async Task<string?> StakeAsync(long betId, ulong userId, int option, double amount)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var bet = await db.Bets.FindAsync(betId);
            if (bet is null || bet.State != BetStates.Open || time.GetUtcNow() >= bet.ClosesAt)
                return "Betting on that has closed.";
            if (userId == bet.CreatorId && !bet.CreatorCanBet)
                return "You can't bet on your own bet.";

            var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefCommands.ModuleId);
            var mine = await db.BetStakes.Where(s => s.BetId == betId && s.UserId == userId).ToListAsync();
            if (mine.Any(s => s.Option != option))
                return $"You already bet on **{bet.Options[mine[0].Option]}**; one option per bet.";
            if (mine.Sum(s => s.Amount) + amount > rules.BetMaxStake)
                return $"You can stake at most {Format(bet.GuildId, rules.BetMaxStake)} on one bet; you have {Format(bet.GuildId, mine.Sum(s => s.Amount))} on this one.";

            if (!await points.TrySpendAsync(bet.GuildId, userId, amount, $"bet {betId}"))
                return $"You don't have {Format(bet.GuildId, amount)}.";

            db.BetStakes.Add(new() { BetId = betId, UserId = userId, Option = option, Amount = amount, CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            await RenderAsync(db, bet);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Resolving an already resolved bet re-resolves it: the old payouts are reverted first.
    public async Task<string> ResolveAsync(long betId, int winner, ulong actorId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var bet = (await db.Bets.FindAsync(betId))!;
            if (bet.State != BetStates.Open)
                await RevertCoreAsync(db, bet);

            var stakes = await db.BetStakes.Where(s => s.BetId == betId).ToListAsync();
            var payouts = BetMath.Resolve(stakes, winner, bet.CreatorId, bet.CreatorCutPercent, bet.HouseCutPercent);
            await PayAsync(db, bet, payouts);

            bet.State = BetStates.Resolved;
            bet.WinningOption = winner;
            bet.ResolvedById = actorId;
            bet.ResolvedAt = time.GetUtcNow();
            if (bet.ClosesAt > bet.ResolvedAt)
                bet.ClosesAt = bet.ResolvedAt.Value;
            await db.SaveChangesAsync();
            await RenderAsync(db, bet);

            return payouts.All(p => p.Kind == BetPayoutKinds.Refund)
                ? $"Nobody picked **{bet.Options[winner]}**, so every stake was refunded."
                : $"**{bet.Options[winner]}** wins; {Format(bet.GuildId, stakes.Sum(s => s.Amount))} was in the pool.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> CancelAsync(long betId, ulong actorId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var bet = (await db.Bets.FindAsync(betId))!;
            await CancelCoreAsync(db, bet, actorId);
            return "Cancelled; every stake was refunded.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // Takes back everything a resolution or cancellation paid and reopens the bet for a result.
    public async Task<string> RevertAsync(long betId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var bet = (await db.Bets.FindAsync(betId))!;
            if (bet.State == BetStates.Open)
                return "That bet hasn't been resolved or cancelled.";

            await RevertCoreAsync(db, bet);
            await db.SaveChangesAsync();
            await RenderAsync(db, bet);
            return "Reverted: payouts were taken back and the bet is waiting for a result again.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasStakedAsync(long betId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BetStakes.AnyAsync(s => s.BetId == betId && s.UserId == userId);
    }

    public async Task<MessageProperties> MessageAsync(Bet bet)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var (embed, components) = await BuildAsync(db, bet);
        return new() { Embeds = [embed], Components = components };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Sweep, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping bets failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var closed = await db.Bets.Where(b => b.State == BetStates.Open && b.ClosesAt <= now).ToListAsync(ct);

            foreach (var bet in closed)
            {
                var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefCommands.ModuleId);
                if (now - bet.ClosesAt >= TimeSpan.FromDays(rules.BetAutoCancelDays))
                    await CancelCoreAsync(db, bet, actorId: null);
                else if (!bet.ClosedShown)
                {
                    bet.ClosedShown = true;
                    await db.SaveChangesAsync(ct);
                    await RenderAsync(db, bet);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CancelCoreAsync(BotDbContext db, Bet bet, ulong? actorId)
    {
        if (bet.State != BetStates.Open)
            await RevertCoreAsync(db, bet);

        var stakes = await db.BetStakes.Where(s => s.BetId == bet.Id).ToListAsync();
        await PayAsync(db, bet, BetMath.Refunds(stakes));

        bet.State = BetStates.Cancelled;
        bet.ResolvedById = actorId;
        bet.ResolvedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        await RenderAsync(db, bet);
    }

    private async Task RevertCoreAsync(BotDbContext db, Bet bet)
    {
        var paid = await db.BetPayouts.Where(p => p.BetId == bet.Id && !p.Reversed).ToListAsync();
        foreach (var payout in paid)
        {
            // A clawback may leave a balance below zero; activity pays it off.
            await points.AwardAsync(bet.GuildId, payout.UserId, -payout.Amount, PointEntryKinds.Bet, $"bet {bet.Id} {payout.Kind} reverted");
            payout.Reversed = true;
        }

        bet.State = BetStates.Open;
        bet.WinningOption = null;
        bet.ResolvedById = null;
        bet.ResolvedAt = null;
    }

    private async Task PayAsync(BotDbContext db, Bet bet, IReadOnlyList<Payout> payouts)
    {
        var now = time.GetUtcNow();
        foreach (var payout in payouts.Where(p => p.Amount > 0))
        {
            await points.AwardAsync(bet.GuildId, payout.UserId, payout.Amount, PointEntryKinds.Bet, $"bet {bet.Id} {payout.Kind}");
            db.BetPayouts.Add(new() { BetId = bet.Id, UserId = payout.UserId, Amount = payout.Amount, Kind = payout.Kind, CreatedAt = now });
        }
    }

    private async Task RenderAsync(BotDbContext db, Bet bet)
    {
        if (bet.MessageId is not { } messageId)
            return;

        var (embed, components) = await BuildAsync(db, bet);
        try
        {
            await rest.ModifyMessageAsync(bet.ChannelId, messageId, m =>
            {
                m.Embeds = [embed];
                m.Components = components;
            });
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            bet.MessageId = null;
            await db.SaveChangesAsync();
        }
    }

    private async Task<(EmbedProperties Embed, IEnumerable<IMessageComponentProperties> Components)> BuildAsync(BotDbContext db, Bet bet)
    {
        var stakes = await db.BetStakes.Where(s => s.BetId == bet.Id).ToListAsync();
        var now = time.GetUtcNow();
        var taking = bet.State == BetStates.Open && now < bet.ClosesAt;

        var text = new StringBuilder();
        for (var i = 0; i < bet.Options.Length; i++)
        {
            var on = stakes.Where(s => s.Option == i).ToList();
            var mark = bet.WinningOption == i ? "🏆 " : "";
            var people = on.Select(s => s.UserId).Distinct().Count();
            text.AppendLine($"{mark}**{bet.Options[i]}**: {Format(bet.GuildId, on.Sum(s => s.Amount))} from {people} {(people == 1 ? "person" : "people")}");
        }

        text.AppendLine();
        text.AppendLine(bet.State switch
        {
            BetStates.Resolved => $"Resolved by <@{bet.ResolvedById}>: **{bet.Options[bet.WinningOption!.Value]}** wins.",
            BetStates.Cancelled => "Cancelled; every stake was refunded.",
            _ when taking => $"Betting closes <t:{bet.ClosesAt.ToUnixTimeSeconds()}:R>. Pick an option to stake.",
            _ => "Betting has closed; waiting for a result.",
        });
        text.Append($"-# Bet {bet.Id} by <@{bet.CreatorId}> · creator cut {bet.CreatorCutPercent}%{(bet.HouseCutPercent > 0 ? $", house {bet.HouseCutPercent}%" : "")}");

        var embed = new EmbedProperties
        {
            Title = bet.Question,
            Description = text.ToString(),
            Color = bet.State == BetStates.Open ? new(0xF1C40F) : new(0x99AAB5),
        };

        var buttons = new ActionRowProperties();
        for (var i = 0; i < bet.Options.Length; i++)
            buttons.Add(new ButtonProperties($"bet:{bet.Id}:{i}", bet.Options[i], ButtonStyle.Primary) { Disabled = !taking });

        return (embed, [buttons]);
    }

    private string Format(ulong guildId, double amount) => points.Rules(guildId).Format(amount);
}
