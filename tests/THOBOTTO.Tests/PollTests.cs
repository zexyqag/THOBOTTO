using THOBOTTO.Events;

namespace THOBOTTO.Tests;

public class PollTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static EventTimeOption Option(long id, int hoursFromNow) => new() { Id = id, StartsAt = Now.AddHours(hoursFromNow) };

    private static EventTimeVote Vote(long option, ulong user) => new() { OptionId = option, UserId = user };

    [Fact]
    public void Most_votes_wins()
    {
        var winner = PollMath.Winner([Option(1, 10), Option(2, 20)], [Vote(1, 1), Vote(2, 1), Vote(2, 2)], Now);
        Assert.Equal(2, winner!.Id);
    }

    [Fact]
    public void A_tie_goes_to_the_earliest_time()
    {
        var winner = PollMath.Winner([Option(1, 20), Option(2, 10)], [Vote(1, 1), Vote(2, 2)], Now);
        Assert.Equal(2, winner!.Id);
    }

    [Fact]
    public void Times_already_past_dont_count()
    {
        var winner = PollMath.Winner([Option(1, -1), Option(2, 10)], [Vote(1, 1), Vote(1, 2), Vote(2, 3)], Now);
        Assert.Equal(2, winner!.Id);
    }

    [Fact]
    public void No_votes_means_no_winner()
    {
        Assert.Null(PollMath.Winner([Option(1, 10)], [], Now));
    }
}
