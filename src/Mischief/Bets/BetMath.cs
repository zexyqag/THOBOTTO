namespace THOBOTTO.Mischief.Bets;

public sealed record Payout(ulong UserId, double Amount, string Kind);

public static class BetMath
{
    // Pari-mutuel: the pool, less the creator's and the house's cut, goes to those who picked the
    // winner in proportion to their stakes. Nobody on the winner means everyone gets their stake back
    // and nobody takes a cut. The house cut is paid to nobody.
    public static IReadOnlyList<Payout> Resolve(IReadOnlyList<BetStake> stakes, int winner, ulong creatorId, double creatorCutPercent, double houseCutPercent)
    {
        var onWinner = stakes.Where(s => s.Option == winner).ToList();
        if (onWinner.Count == 0)
            return Refunds(stakes);

        var pool = stakes.Sum(s => s.Amount);
        var cut = pool * creatorCutPercent / 100;
        var house = pool * houseCutPercent / 100;
        var shared = pool - cut - house;
        var winningTotal = onWinner.Sum(s => s.Amount);

        var payouts = onWinner
            .GroupBy(s => s.UserId)
            .Select(g => new Payout(g.Key, shared * g.Sum(s => s.Amount) / winningTotal, BetPayoutKinds.Win))
            .ToList();
        if (cut > 0)
            payouts.Add(new(creatorId, cut, BetPayoutKinds.Cut));
        return payouts;
    }

    public static IReadOnlyList<Payout> Refunds(IReadOnlyList<BetStake> stakes)
        => stakes.GroupBy(s => s.UserId).Select(g => new Payout(g.Key, g.Sum(s => s.Amount), BetPayoutKinds.Refund)).ToList();
}
