using THOBOTTO.Games;
using THOBOTTO.Helpers;
using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class QueueRemovalTests
{
    private static Track Song(string title) => new(title, title, "", null, 1000, false, "youtube", 1);

    [Fact]
    public void Numbers_run_over_the_added_songs_then_the_playlists()
    {
        List<Track> added = [Song("a1"), Song("a2")];
        List<Track> later = [Song("p1"), Song("p2")];
        Assert.Equal("a2", MusicPlayer.RemoveAt(added, later, 2)!.Title);
        Assert.Equal("p1", MusicPlayer.RemoveAt(added, later, 2)!.Title);
        Assert.Null(MusicPlayer.RemoveAt(added, later, 3));
        Assert.Null(MusicPlayer.RemoveAt(added, later, 0));
        Assert.Equal(["a1"], added.Select(t => t.Title));
        Assert.Equal(["p2"], later.Select(t => t.Title));
    }

    [Fact]
    public void A_role_colour_is_read_as_hex_with_blurple_for_anything_else()
    {
        Assert.Equal(0xFF8800, GameRules.ColourValue(" #ff8800 "));
        Assert.Equal(0x5865F2, GameRules.ColourValue("orange"));
        Assert.Equal(0x5865F2, GameRules.ColourValue("#fff"));
    }
}
