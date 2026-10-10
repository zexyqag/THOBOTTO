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
}
