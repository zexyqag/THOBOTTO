using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class SkipVotesTests
{
    [Fact]
    public void A_share_of_those_present_rounded_up_and_never_none()
    {
        Assert.Equal(2, SkipVotes.Needed(3, 50));
        Assert.Equal(2, SkipVotes.Needed(4, 50));
        Assert.Equal(4, SkipVotes.Needed(4, 100));
        Assert.Equal(1, SkipVotes.Needed(4, 0));
        Assert.Equal(1, SkipVotes.Needed(0, 50));
    }
}
