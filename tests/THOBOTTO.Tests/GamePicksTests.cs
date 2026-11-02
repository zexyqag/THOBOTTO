using THOBOTTO.Games;

namespace THOBOTTO.Tests;

public class GamePicksTests
{
    private static readonly Game[] Games =
    [
        new() { Id = 6, GuildId = 1, Name = "Valheim", Emoji = "🪓" },
        new() { Id = 7, GuildId = 1, Name = "Lethal Company" },
        new() { Id = 8, GuildId = 1, Name = "Lethal League" },
    ];

    [Fact]
    public void Games_are_named_by_name_label_or_id_each_once()
    {
        var (picked, problem) = GamePicks.Parse("🪓 Valheim, lethal company, 6", Games, GamePicks.Names);
        Assert.Null(problem);
        Assert.Equal([6L, 7L], picked.Select(g => g.Id));
    }

    [Fact]
    public void An_unknown_name_says_so()
        => Assert.Contains("`Minecraft` isn't one of the games", GamePicks.Parse("Valheim, Minecraft", Games, GamePicks.Names).Problem);

    [Fact]
    public void A_name_two_servers_share_has_to_be_picked_from_the_list()
    {
        (string Server, Game Game)[] shared = [("A", Games[0]), ("B", new() { Id = 9, GuildId = 2, Name = "Valheim" })];
        Assert.Contains("more than one server", GamePicks.Parse("Valheim", shared, s => GamePicks.Names(s.Game)).Problem);
    }

    [Fact]
    public void The_one_being_typed_is_completed_after_those_picked_and_without_them()
    {
        var choices = GamePicks.Complete("Valheim, leth", Games, GameDirectory.Label, GamePicks.Names).Select(c => c.Name).ToList();
        Assert.Equal(["Valheim, Lethal Company", "Valheim, Lethal League"], choices);
        Assert.DoesNotContain(GamePicks.Complete("Valheim, ", Games, GameDirectory.Label, GamePicks.Names), c => c.Name.EndsWith("Valheim", StringComparison.Ordinal));
    }
}
