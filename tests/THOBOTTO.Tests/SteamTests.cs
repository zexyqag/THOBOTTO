using System.Text.Json;

using THOBOTTO.Steam;

namespace THOBOTTO.Tests;

public class SteamTests
{
    [Theory]
    [InlineData("https://steamcommunity.com/openid/id/76561197960435530", 76561197960435530UL)]
    [InlineData("https://evil.example/openid/id/76561197960435530", null)]
    [InlineData("https://steamcommunity.com/openid/id/123", null)]
    [InlineData(null, null)]
    public void The_identity_steam_vouches_for_names_the_account(string? claimed, ulong? expected)
        => Assert.Equal(expected, SteamSignIn.SteamId(claimed));

    [Fact]
    public void Owned_games_are_read_and_private_ones_are_none()
    {
        using var shown = JsonDocument.Parse("""{"response":{"game_count":2,"games":[{"appid":892970,"name":"Valheim","playtime_forever":7260},{"appid":10,"name":"Counter-Strike","playtime_forever":0}]}}""");
        Assert.Equal([new OwnedGame(892970, "Valheim", 7260), new OwnedGame(10, "Counter-Strike", 0)], SteamClient.Owned(shown.RootElement));
        using var none = JsonDocument.Parse("""{"response":{"game_count":0}}""");
        Assert.Empty(SteamClient.Owned(none.RootElement)!);
        using var hidden = JsonDocument.Parse("""{"response":{}}""");
        Assert.Null(SteamClient.Owned(hidden.RootElement));
    }

    [Fact]
    public void A_store_page_says_whether_its_played_together()
    {
        using var together = JsonDocument.Parse("""{"892970":{"success":true,"data":{"categories":[{"id":2,"description":"Single-player"},{"id":38,"description":"Online Co-op"}]}}}""");
        Assert.True(SteamClient.Together(together.RootElement, 892970));
        using var alone = JsonDocument.Parse("""{"70":{"success":true,"data":{"categories":[{"id":2,"description":"Single-player"}]}}}""");
        Assert.False(SteamClient.Together(alone.RootElement, 70));
        using var gone = JsonDocument.Parse("""{"5":{"success":false}}""");
        Assert.Null(SteamClient.Together(gone.RootElement, 5));
    }

    [Theory]
    [InlineData("Counter-Strike 2", "Counter Strike 2")]
    [InlineData("DOOM", "Doom")]
    [InlineData("Satisfactory™", "Satisfactory")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", "Tom Clancys Rainbow Six Siege")]
    public void Names_are_the_same_game_whatever_their_case_punctuation_and_marks(string ours, string steams)
        => Assert.True(SteamNames.Same(ours, steams));

    [Fact]
    public void Another_name_is_another_game()
        => Assert.False(SteamNames.Same("CS2", "Counter-Strike 2"));

    [Fact]
    public void A_session_says_how_many_own_it_and_who_is_in_without_it()
    {
        Assert.Equal("🛒 On Steam: 1 here owns it", SteamSessionLines.Line(1, []));
        Assert.Equal("🛒 On Steam: 4 here own it · in without it: <@5>, <@6>", SteamSessionLines.Line(4, [5, 6]));
    }

    [Theory]
    [InlineData(0, "not played yet")]
    [InlineData(45, "45 min")]
    [InlineData(7260, "121 h")]
    public void Playtime_reads_in_hours(int minutes, string expected)
        => Assert.Equal(expected, SteamCommands.Played(minutes));
}
