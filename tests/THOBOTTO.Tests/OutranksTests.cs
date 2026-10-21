using THOBOTTO.Access;

namespace THOBOTTO.Tests;

public class OutranksTests
{
    private const ulong Owner = 1;

    [Fact]
    public void The_owner_outranks_everyone_and_no_one_outranks_the_owner()
    {
        Assert.True(AccessControl.Outranks(Owner, (Owner, 0), (2, 100), allowEqual: false));
        Assert.False(AccessControl.Outranks(Owner, (2, 100), (Owner, 0), allowEqual: true));
        Assert.True(AccessControl.Outranks(Owner, (Owner, 0), (Owner, 0), allowEqual: false));
    }

    [Fact]
    public void A_higher_rank_wins_and_the_same_rank_only_when_allowed()
    {
        Assert.True(AccessControl.Outranks(Owner, (2, 5), (3, 4), allowEqual: false));
        Assert.False(AccessControl.Outranks(Owner, (2, 4), (3, 5), allowEqual: true));
        Assert.False(AccessControl.Outranks(Owner, (2, 5), (3, 5), allowEqual: false));
        Assert.True(AccessControl.Outranks(Owner, (2, 5), (3, 5), allowEqual: true));
    }

    [Fact]
    public void A_members_rank_is_their_highest_role_or_everyone_without_any()
    {
        Assert.Equal(0, AccessControl.Rank(0, []));
        Assert.Equal(7, AccessControl.Rank(0, [3, 7, 2]));
        Assert.Equal(0, AccessControl.Rank(0, [-1]));
    }
}
