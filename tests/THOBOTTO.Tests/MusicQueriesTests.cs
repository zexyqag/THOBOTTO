using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class MusicQueriesTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", "https://www.youtube.com/watch?v=abc")]
    [InlineData("youtu.be/abc?si=xyz", "https://youtu.be/abc?si=xyz")]
    [InlineData("www.youtube.com/watch?v=abc&list=PL1", "https://www.youtube.com/watch?v=abc&list=PL1")]
    [InlineData("music.youtube.com/watch?v=abc", "https://music.youtube.com/watch?v=abc")]
    [InlineData("open.spotify.com/track/2JiDi0qAXsPwhPqA2qaK", "https://open.spotify.com/track/2JiDi0qAXsPwhPqA2qaK")]
    [InlineData("scsearch:abba waterloo", "scsearch:abba waterloo")]
    [InlineData("abba waterloo", "ytsearch:abba waterloo")]
    [InlineData("hello.world", "ytsearch:hello.world")]
    [InlineData("  abba  ", "ytsearch:abba")]
    public void Links_go_as_typed_bare_links_get_a_scheme_and_words_go_to_the_search(string typed, string identifier)
        => Assert.Equal(identifier, MusicQueries.Identifier(typed, "ytsearch"));

    [Fact]
    public void A_picked_source_prefixes_the_words()
    {
        Assert.Equal("spsearch:abba", MusicQueries.Identifier("abba", MusicQueries.Prefix(SearchOn.Spotify)));
        Assert.Equal("ytmsearch", MusicQueries.Prefix(SearchOn.YouTubeMusic));
        Assert.Equal("scsearch", MusicQueries.Prefix(SearchOn.SoundCloud));
        Assert.Equal("ytsearch", MusicQueries.Prefix(SearchOn.YouTube));
    }
}
