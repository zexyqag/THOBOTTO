using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class RecentIdsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Remembers_an_id_for_a_while_and_forgets_the_old_ones_as_new_ones_come()
    {
        var gone = new RecentIds(TimeSpan.FromHours(1));
        gone.Note(1, Now);
        gone.Note(2, Now.AddMinutes(30));

        Assert.True(gone.TryGet(1, out var at));
        Assert.Equal(Now, at);
        gone.Note(3, Now.AddMinutes(61));
        Assert.False(gone.TryGet(1, out _));
        Assert.True(gone.TryGet(2, out _));
        Assert.True(gone.TryGet(3, out _));
    }
}
