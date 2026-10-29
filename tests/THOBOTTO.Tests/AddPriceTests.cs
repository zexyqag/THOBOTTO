using THOBOTTO.Points;

namespace THOBOTTO.Tests;

public class AddPriceTests
{
    [Fact]
    public void Each_of_your_own_waiting_adds_to_the_price_and_a_playlist_counts_as_several_songs()
    {
        Assert.Equal(1, AddPrice.Of(1, 1, 0));
        Assert.Equal(4, AddPrice.Of(1, 1, 3));
        Assert.Equal(3, AddPrice.Of(1, 1, 0, playlistLike: 3));
        Assert.Equal(5, AddPrice.Of(1, 1, 2, playlistLike: 3));
        Assert.Equal(1, AddPrice.Of(1, 0, 9));
    }

    [Fact]
    public void Adding_is_free_with_few_enough_listening()
    {
        Assert.True(AddPrice.Free(2, 2));
        Assert.True(AddPrice.Free(1, 2));
        Assert.False(AddPrice.Free(3, 2));
        Assert.False(AddPrice.Free(1, 0));
    }
}
