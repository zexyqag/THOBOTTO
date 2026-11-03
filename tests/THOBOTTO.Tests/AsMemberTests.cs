using THOBOTTO.Discord;

namespace THOBOTTO.Tests;

public class AsMemberTests
{
    [Fact]
    public void A_nickname_gets_the_username_beside_it_when_asked()
    {
        Assert.Equal("Yeeti (yeeti123)", AsMember.NameOf("Yeeti", "yeeti123", withUsername: true));
        Assert.Equal("Yeeti", AsMember.NameOf("Yeeti", "yeeti123", withUsername: false));
    }

    [Fact]
    public void A_name_that_is_the_username_is_shown_once()
        => Assert.Equal("Yeeti", AsMember.NameOf("Yeeti", "yeeti", withUsername: true));
}
