using THOBOTTO.Voice;

namespace THOBOTTO.Tests;

public class ChannelNamerTests
{
    [Fact]
    public void The_game_most_people_play_names_the_channel()
        => Assert.Equal("Valorant", ChannelNamer.MajorityGame([["Valorant"], ["Valorant", "Spotify"], ["Minecraft"]]));

    [Fact]
    public void Half_the_people_is_enough()
        => Assert.Equal("Minecraft", ChannelNamer.MajorityGame([["Minecraft"], []]));

    [Fact]
    public void Fewer_than_half_is_not()
        => Assert.Null(ChannelNamer.MajorityGame([["Minecraft"], [], []]));

    [Fact]
    public void A_tie_goes_by_name()
        => Assert.Equal("Apex", ChannelNamer.MajorityGame([["Valorant"], ["Apex"]]));

    [Fact]
    public void Nobody_playing_names_nothing()
        => Assert.Null(ChannelNamer.MajorityGame([]));

    [Fact]
    public void A_game_counts_once_per_person()
        => Assert.Null(ChannelNamer.MajorityGame([["Valorant", "Valorant"], [], []]));

    private static ChannelScene Scene(string? pinned = null, string? game = null, bool live = false, string? music = null, bool quiet = false)
        => new("Ana's channel", pinned, game, live, music, quiet);

    [Theory]
    [InlineData(null, null, false, null, false, "Ana's channel")]
    [InlineData(null, "Valorant", false, null, false, "🎮 Valorant")]
    [InlineData(null, "Valorant", true, null, false, "🔴 Valorant")]
    [InlineData(null, null, true, null, false, "🔴 Live")]
    [InlineData(null, "Valorant", false, "DJ Volume · Daft Punk", false, "🎮 Valorant")]
    [InlineData(null, null, false, "DJ Volume · Daft Punk", false, "🎵 DJ Volume · Daft Punk")]
    [InlineData(null, null, false, null, true, "💤 Quiet")]
    [InlineData("The den", "Valorant", true, null, false, "The den")]
    [InlineData("{game} with the lads", "Valorant", false, null, false, "Valorant with the lads")]
    [InlineData("{game} with the lads", null, false, null, false, "Ana's channel")]
    public void Names_follow_what_goes_on(string? pinned, string? game, bool live, string? music, bool quiet, string expected)
        => Assert.Equal(expected, ChannelNamer.NameFor(Scene(pinned, game, live, music, quiet), byActivity: true));

    [Fact]
    public void Without_activity_names_only_the_owners_name_counts()
    {
        Assert.Equal("Ana's channel", ChannelNamer.NameFor(Scene(game: "Valorant"), byActivity: false));
        Assert.Equal("The den", ChannelNamer.NameFor(Scene(pinned: "The den"), byActivity: false));
    }
}
