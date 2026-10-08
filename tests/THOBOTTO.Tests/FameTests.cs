using THOBOTTO.Fame;

namespace THOBOTTO.Tests;

public class FameTests
{
    [Fact]
    public void Reads_a_message_time_from_its_id()
    {
        // Discord's documented example: 175928847299117063 was created 2016-04-30 11:18:25.796 UTC.
        Assert.Equal(new DateTimeOffset(2016, 4, 30, 11, 18, 25, 796, TimeSpan.Zero), HallOfFame.CreatedAt(175928847299117063));
    }
}
