using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class QueueVotesTests
{
    [Fact]
    public void Voted_items_from_the_threshold_up_go_first_most_voted_first_the_rest_keep_their_order()
    {
        var queue = new[] { ("a", 1), ("b", 3), ("c", 0), ("d", 2), ("e", 3) };
        Assert.Equal(["b", "e", "d", "a", "c"], QueueVotes.Reordered(queue, i => i.Item2, 2).Select(i => i.Item1));
        Assert.Equal(["a", "b", "c", "d", "e"], QueueVotes.Reordered(queue, i => i.Item2, 4).Select(i => i.Item1));
    }
}
