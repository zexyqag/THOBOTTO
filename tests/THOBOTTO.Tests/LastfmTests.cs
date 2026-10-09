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
