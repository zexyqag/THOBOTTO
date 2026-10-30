using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Happenings;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief.Bets;

// Every change to a bet goes through here, one at a time, so stakes, resolutions and reverts
// never interleave. A sweep marks bets closed on their message and cancels forgotten ones.
public sealed class BetBook(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    SettingsStore settings,
    PointsEngine points,
    Notifier notifier,
    AccessControl access,
    GatewayClient gateway,
    HappeningForum forum,
    TimeProvider time,
    ILogger<BetBook> logger) : BackgroundService
{
    public static readonly HappeningKind Happening = new("bet", "Bet", "🎲", "Bets");
    public const int MaxOptions = 5;

    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Bet> CreateAsync(ulong guildId, ulong channelId, ulong creatorId, string question, string[] options, TimeSpan duration)
    {
        var rules = await settings.GetAsync<MischiefRules>(guildId, MischiefModule.ModuleId);
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

            var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefModule.ModuleId);
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
            await NotifyStakersAsync(bet, stakes, $"the bet \"{bet.Question}\" was resolved: **{bet.Options[winner]}** wins");

            return payouts.All(p => p.Kind == BetPayoutKinds.Refund)
                ? $"Nobody picked **{bet.Options[winner]}**, so every stake was refunded."
                : $"**{bet.Options[winner]}** wins; the pool held {Format(bet.GuildId, stakes.Sum(s => s.Amount))}.";
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

    // While a creator's own result can still be called unfair.
    public static bool Disputable(Bet bet, DateTimeOffset now, int hours)
        => hours > 0 && bet.State == BetStates.Resolved && bet.ResolvedById == bet.CreatorId && bet.ResolvedAt is { } at && now - at < TimeSpan.FromHours(hours);

    // A bettor calls the creator's result unfair: noted on the bet, and those who manage bets are told to look.
    public async Task<string> DisputeAsync(long betId, ulong userId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var bet = (await db.Bets.FindAsync(betId))!;
            var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefModule.ModuleId);
            var now = time.GetUtcNow();
            if (!Disputable(bet, now, rules.BetDisputeHours))
            {
                await RenderAsync(db, bet);
                return rules.BetDisputeHours == 0 ? "Calling a result unfair is off here."
                    : bet.State != BetStates.Resolved ? "That bet isn't resolved."
                    : bet.ResolvedById != bet.CreatorId ? "Someone who manages bets resolved it; there's nobody further to turn to."
                    : "The time to call it unfair has passed.";
            }
            if (!await db.BetStakes.AnyAsync(s => s.BetId == betId && s.UserId == userId))
                return "Only those who staked on it can call it unfair.";
            if (await db.BetDisputes.AnyAsync(d => d.BetId == betId && d.UserId == userId))
                return "You've called it unfair already; those who manage bets have been told.";
            db.BetDisputes.Add(new() { BetId = betId, UserId = userId, CreatedAt = now });
            await db.SaveChangesAsync();
            var count = await db.BetDisputes.CountAsync(d => d.BetId == betId);
            await RenderAsync(db, bet);
            await NotifyManagersAsync(bet, userId, count);
            return $"Noted; those who manage bets have been told ({count} so far).";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task NotifyManagersAsync(Bet bet, ulong by, int count)
    {
        if (!gateway.Cache.Guilds.TryGetValue(bet.GuildId, out var guild))
            return;
        var managers = new List<ulong>();
        foreach (var user in guild.Users.Values)
            if (!user.IsBot && user.Id != bet.CreatorId && await access.CanAsync(guild, user, BotPermissions.ManageBets))
                managers.Add(user.Id);
        await notifier.NotifyAsync(bet.GuildId, NotificationTopics.BetDisputes, managers,
            $"<@{by}> called the bet \"{bet.Question}\" unfair ({count} so far; its creator resolved it): resolve it again or revert it",
            Notifier.Link(bet.GuildId, bet.ChannelId, bet.MessageId));
    }

    public async Task<bool> HasStakedAsync(long betId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BetStakes.AnyAsync(s => s.BetId == betId && s.UserId == userId);
    }

    // The card posted where the bet was made: it's shown in the happenings forum too.
    public async Task PostedAsync(Bet bet, ulong messageId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Bets.Attach(bet);
            bet.MessageId = messageId;
            await db.SaveChangesAsync();
            var (embed, components) = await BuildAsync(db, bet);
            await forum.ShowAsync(bet.GuildId, Happening.Id, bet.Id, HappeningStates.Open, new(bet.Question, [embed], components));
        }
        finally
        {
            _gate.Release();
        }
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
                var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefModule.ModuleId);
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
        await NotifyStakersAsync(bet, stakes, $"the bet \"{bet.Question}\" was cancelled and your stake refunded");
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

    private Task NotifyStakersAsync(Bet bet, IEnumerable<BetStake> stakes, string text)
        => notifier.NotifyAsync(bet.GuildId, NotificationTopics.BetResults, stakes.Select(s => s.UserId), text, Notifier.Link(bet.GuildId, bet.ChannelId, bet.MessageId));

    // Every copy of the card: where it was made, and in the forum.
    private async Task RenderAsync(BotDbContext db, Bet bet)
    {
        var (embed, components) = await BuildAsync(db, bet);
        var state = bet.State switch { BetStates.Resolved => HappeningStates.Done, BetStates.Cancelled => HappeningStates.Cancelled, _ => HappeningStates.Open };
        await forum.ShowAsync(bet.GuildId, Happening.Id, bet.Id, state, new(bet.Question, [embed], components));
        if (bet.MessageId is not { } messageId)
            return;
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
        var disputes = await db.BetDisputes.CountAsync(d => d.BetId == bet.Id);
        if (disputes > 0)
            text.AppendLine($"⚖️ Called unfair by {disputes}; someone with `{BotPermissions.ManageBets}` will look.");
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

        var rules = await settings.GetAsync<MischiefRules>(bet.GuildId, MischiefModule.ModuleId);
        if (Disputable(bet, now, rules.BetDisputeHours))
            return (embed, [buttons, new ActionRowProperties { new ButtonProperties($"betdispute:{bet.Id}", "⚖️ Unfair?", ButtonStyle.Secondary) }]);
        return (embed, [buttons]);
    }

    private string Format(ulong guildId, double amount) => points.Rules(guildId).Format(amount);
}
