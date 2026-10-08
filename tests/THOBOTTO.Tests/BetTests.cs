using THOBOTTO.Mischief.Bets;

namespace THOBOTTO.Tests;

public class BetTests
{
    private const ulong Creator = 1, Ann = 2, Bob = 3, Cat = 4;

    private static BetStake Stake(ulong user, int option, double amount) => new() { UserId = user, Option = option, Amount = amount };

    [Fact]
    public void Winners_share_the_pool_less_the_cuts_by_stake()
    {
        // Pool 200: Ann 30 and Bob 10 on 0, Cat 160 on 1. Creator cut 5% = 10, house 0.
        var payouts = BetMath.Resolve([Stake(Ann, 0, 30), Stake(Bob, 0, 10), Stake(Cat, 1, 160)], winner: 0, Creator, 5, 0);

        Assert.Equal(142.5, payouts.Single(p => p.UserId == Ann).Amount, 9);
        Assert.Equal(47.5, payouts.Single(p => p.UserId == Bob).Amount, 9);
        Assert.Equal(10, payouts.Single(p => p.Kind == BetPayoutKinds.Cut && p.UserId == Creator).Amount, 9);
        Assert.DoesNotContain(payouts, p => p.UserId == Cat);
    }

    [Fact]
    public void The_house_cut_is_paid_to_nobody()
    {
        var payouts = BetMath.Resolve([Stake(Ann, 0, 50), Stake(Bob, 1, 50)], winner: 0, Creator, 5, 10);

        Assert.Equal(85, payouts.Single(p => p.UserId == Ann).Amount, 9);
        Assert.Equal(100 - 15, payouts.Sum(p => p.Amount) - 5, 9);
    }

    [Fact]
    public void Several_stakes_of_one_person_add_up()
    {
        var payouts = BetMath.Resolve([Stake(Ann, 0, 10), Stake(Ann, 0, 10), Stake(Bob, 1, 20)], winner: 0, Creator, 0, 0);

        Assert.Equal(40, Assert.Single(payouts).Amount, 9);
    }

    [Fact]
    public void Nobody_on_the_winner_refunds_everyone_without_a_cut()
    {
        var payouts = BetMath.Resolve([Stake(Ann, 0, 10), Stake(Ann, 0, 5), Stake(Bob, 0, 20)], winner: 1, Creator, 5, 5);

        Assert.All(payouts, p => Assert.Equal(BetPayoutKinds.Refund, p.Kind));
        Assert.Equal(15, payouts.Single(p => p.UserId == Ann).Amount);
        Assert.Equal(20, payouts.Single(p => p.UserId == Bob).Amount);
    }
}
