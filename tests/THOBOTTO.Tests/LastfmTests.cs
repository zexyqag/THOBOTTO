using THOBOTTO.Lastfm;
using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class LastfmTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static Track T(string title, string author, string source = "youtube", long ms = 213_000, bool stream = false)
        => new("enc", title, author, null, ms, stream, source, 1);

    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", "Rick Astley", "youtube", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Never Gonna Give You Up", "Rick Astley - Topic", "youtube", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Impermanence", "Architects, Winston McCall", "spotify", "Architects", "Impermanence")]
    [InlineData("Forever Young (2019 Remaster)", "Alphaville", "spotify", "Alphaville", "Forever Young")]
    [InlineData("Thunderstruck (Live) [Lyrics]", "AC/DC", "youtube", "AC/DC", "Thunderstruck (Live)")]
    public void Names_are_tidied_for_lastfm(string title, string author, string source, string artist, string song)
    {
        var scrobble = TrackNames.From(T(title, author, source), At)!;
        Assert.Equal((artist, song), (scrobble.Artist, scrobble.Title));
        Assert.Equal(213, scrobble.Seconds);
    }

    [Fact]
    public void Streams_and_short_tracks_are_not_scrobbled()
    {
        Assert.Null(TrackNames.From(T("Radio", "Station", stream: true), At));
        Assert.Null(TrackNames.From(T("Jingle", "Someone", ms: 29_000), At));
    }

    [Fact]
    public void Calls_are_signed_as_lastfm_expects()
        => Assert.Equal("77d61cbdb9bf03f975d2d3a6ef001d4c", LastfmClient.Sign(new Dictionary<string, string>
        {
            ["method"] = "auth.getSession",
            ["token"] = "TOK",
            ["api_key"] = "KEY",
            ["format"] = "json",
        }, "secret"));
}

public class LastfmResponseTests
{
    private static IReadOnlyList<Song> Read(string json, string list) => LastfmClient.Songs(System.Text.Json.JsonDocument.Parse(json).RootElement, list);

    [Fact]
    public void Reads_top_tracks()
        => Assert.Equal([new Song("Toto", "Africa"), new Song("ABBA", "Dancing Queen")], Read("""
            {"toptracks":{"track":[
              {"name":"Africa","playcount":"12","artist":{"name":"Toto","mbid":"","url":"https://www.last.fm/music/Toto"},"@attr":{"rank":"1"}},
              {"name":"Dancing Queen","playcount":"9","artist":{"name":"ABBA","mbid":"","url":""},"@attr":{"rank":"2"}}],
             "@attr":{"user":"ana","page":"1","total":"2"}}}
            """, "toptracks"));

    [Fact]
    public void Reads_a_single_track_given_as_an_object_and_text_artists()
        => Assert.Equal([new Song("Rick Astley", "Never Gonna Give You Up")], Read("""
            {"lovedtracks":{"track":{"name":"Never Gonna Give You Up","artist":{"#text":"Rick Astley"}},"@attr":{"total":"1"}}}
            """, "lovedtracks"));

    [Fact]
    public void Nothing_there_is_no_songs()
    {
        Assert.Empty(Read("""{"lovedtracks":{"track":[],"@attr":{"total":"0"}}}""", "lovedtracks"));
        Assert.Empty(Read("""{"similartracks":{"@attr":{"artist":"x"}}}""", "similartracks"));
    }
}
