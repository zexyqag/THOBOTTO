using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class DecidingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset[] Times = [Now.AddDays(1), Now.AddDays(2), Now.AddDays(3)];

    [Fact]
    public void Enough_people_settle_it_at_once_on_the_earliest_such_time()
    {
        Assert.Equal(1, Deciding.Settled(Deciding.AsSoonAsEnough, Times, [2, 4, 5], enough: 4, Now, closed: false));
        Assert.Null(Deciding.Settled(Deciding.AsSoonAsEnough, Times, [2, 3, 3], enough: 4, Now, closed: false));
    }

    [Fact]
    public void Without_enough_people_the_most_votes_win_when_it_closes()
        => Assert.Equal(1, Deciding.Settled(Deciding.AsSoonAsEnough, Times, [2, 3, 3], enough: 4, Now, closed: true));

    [Fact]
    public void Most_votes_wait_for_the_close_and_ties_go_to_the_earliest()
    {
        Assert.Null(Deciding.Settled(Deciding.WhenItCloses, Times, [9, 9, 9], enough: 1, Now, closed: false));
        Assert.Equal(1, Deciding.Settled(Deciding.WhenItCloses, Times, [1, 3, 3], enough: 1, Now, closed: true));
    }

    [Fact]
    public void The_planner_settles_it_whatever_the_votes()
    {
        Assert.Null(Deciding.Settled(Deciding.PlannerDecides, Times, [9, 9, 9], enough: 1, Now, closed: true));
        Assert.Equal(2, Deciding.MostVotes(Times, [1, 1, 2], Now));
    }

    [Fact]
    public void Past_times_and_times_nobody_can_make_never_win()
    {
        Assert.Equal(2, Deciding.Settled(Deciding.WhenItCloses, Times, [5, 0, 1], enough: 1, Now.AddDays(1.5), closed: true));
        Assert.Null(Deciding.Settled(Deciding.WhenItCloses, Times, [0, 0, 0], enough: 1, Now, closed: true));
        Assert.True(Deciding.Lapsed(Times, Now.AddDays(4)));
        Assert.False(Deciding.Lapsed(Times, Now));
    }

    [Theory]
    [InlineData(4, 3, true)]
    [InlineData(4, 2, false)]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, false)]
    public void A_suggested_time_moves_it_when_most_of_those_who_are_in_agree(int inCount, int agreeing, bool moves)
        => Assert.Equal(moves, Moving.Agreed(inCount, agreeing));
}
