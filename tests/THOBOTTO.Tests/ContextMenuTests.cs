using NodaTime;

using THOBOTTO.Games;
using THOBOTTO.Reminders;
using THOBOTTO.Steam;

namespace THOBOTTO.Tests;

public class ContextMenuTests
{
    private static readonly DateTimeZone Copenhagen = DateTimeZoneProviders.Tzdb["Europe/Copenhagen"];
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 2, 12, 0);
    private static readonly string[] Names = ["Valheim", "Lethal Company", "CS2"];

    [Theory]
    [InlineData("anyone up for valheim sat 20:00?", "Valheim", "sat 20:00")]
    [InlineData("Lethal Company or Valheim tomorrow 19:30, who's in", "Valheim, Lethal Company", "tomorrow 19:30")]
    [InlineData("valheim now!", "Valheim", "now")]
    [InlineData("we should play more", "", "")]
    public void A_message_names_its_games_and_time(string text, string games, string when)
    {
        var (named, at) = MessagePlans.Guess(text, Names, Copenhagen, Now);
        Assert.Equal(games, string.Join(", ", named));
        Assert.Equal(when, at);
    }

    [Fact]
    public void Part_of_a_word_isnt_a_game()
        => Assert.Empty(MessagePlans.Guess("cs20 is a code, not a game", Names, Copenhagen, Now).Games);

    [Fact]
    public void Of_what_you_can_play_together_those_the_server_has()
    {
        var common = new CommonGames([1, 2], [], [], [new(892970, "Valheim™", 600), new(1, "Some Game", 5)], Shared: 2, Asked: 2);
        Game[] games = [new() { Id = 6, GuildId = 1, Name = "Valheim" }, new() { Id = 7, GuildId = 1, Name = "Lethal Company" }];
        Assert.Equal(["Valheim"], PlayTogether.Plannable(common, games));
    }

    [Fact]
    public void A_reminder_links_its_message_in_a_server_or_a_dm()
    {
        Assert.Equal("https://discord.com/channels/1/2/3", ReminderBook.Link(1, 2, 3));
        Assert.Equal("https://discord.com/channels/@me/2/3", ReminderBook.Link(0, 2, 3));
    }

    [Theory]
    [InlineData("Smoke Cat!.png", "smoke_cat")]
    [InlineData("x.png", "")]
    [InlineData("a-very-long-name-for-an-emoji-that-goes-on-and-on.gif", "a_very_long_name_for_an_emoji_th")]
    public void A_files_name_becomes_an_emoji_name(string file, string name)
        => Assert.Equal(name, Sounds.ProposeMenu.NameOf(file));
}
