using NetCord.Rest;
using NetCord.Rest.JsonModels;

using THOBOTTO.Games;

namespace THOBOTTO.Tests;

public class GameOnboardingTests
{
    private static readonly Game[] Games =
    [
        new() { Id = 1, GuildId = 9, Name = "Valheim", Emoji = "🪓", RoleId = 101 },
        new() { Id = 2, GuildId = 9, Name = "Lethal Company", RoleId = 102 },
    ];

    private static Func<ulong> Fresh()
    {
        var next = 1000UL;
        return () => next++;
    }

    private static GuildOnboardingPrompt Prompt(ulong id, string title, params (ulong Id, ulong Role)[] options)
        => new(new JsonGuildOnboardingPrompt
        {
            Id = id,
            Title = title,
            InOnboarding = true,
            Options = [.. options.Select(o => new JsonGuildOnboardingPromptOption { Id = o.Id, Title = "x", RoleIds = [o.Role], ChannelIds = [], Emoji = new() })],
        }, 9, null!);

    [Fact]
    public void Each_game_is_an_answer_giving_its_role_and_keeping_its_id()
    {
        var made = GameOnboarding.Question("What do you play?", Games, Prompt(50, "What do you play?", (60, 102)), Fresh());

        var options = made.Options.ToList();
        Assert.Equal((50UL, "What do you play?", true, false), (made.Id!.Value, made.Title, made.InOnboarding, made.SingleSelect));
        Assert.Equal([("Valheim", "🪓", (ulong?)1000), ("Lethal Company", null, 60UL)], options.Select(o => (o.Title, o.EmojiName, o.Id)));
        Assert.Equal([[101UL], [102UL]], options.Select(o => o.RoleIds!.ToArray()));
    }

    [Fact]
    public void Games_sharing_a_role_keep_their_own_answers()
    {
        Game[] sharing = [new() { Id = 1, GuildId = 9, Name = "Valheim", RoleId = 101 }, new() { Id = 2, GuildId = 9, Name = "Lethal Company", RoleId = 101 }];
        var before = new GuildOnboardingPrompt(new JsonGuildOnboardingPrompt
        {
            Id = 50,
            Title = "What do you play?",
            Options = [new() { Id = 60, Title = "Lethal Company", RoleIds = [101], ChannelIds = [], Emoji = new() }, new() { Id = 61, Title = "Valheim", RoleIds = [101], ChannelIds = [], Emoji = new() }],
        }, 9, null!);

        Assert.Equal([61UL, 60UL], GameOnboarding.Question("What do you play?", sharing, before, Fresh()).Options.Select(o => o.Id!.Value));
    }

    [Fact]
    public void A_new_question_and_its_answers_get_fresh_ids()
        => Assert.Equal([1000UL, 1001UL, 1002UL], [.. GameOnboarding.Question("What do you play?", Games, null, Fresh()) is var made ? made.Options.Select(o => o.Id!.Value).Append(made.Id!.Value) : []]);

    [Fact]
    public void Its_question_is_found_by_title_or_by_answers_that_are_all_games()
    {
        var roles = Games.Select(g => g.RoleId).ToHashSet();
        IReadOnlyList<GuildOnboardingPrompt> prompts = [Prompt(1, "Pick channels", (2, 999)), Prompt(3, "Old title", (4, 101), (5, 102))];

        Assert.Equal(3UL, GameOnboarding.Ours(prompts, "What do you play?", roles)?.Id);
        Assert.Equal(1UL, GameOnboarding.Ours(prompts, "Pick channels", roles)?.Id);
        Assert.Null(GameOnboarding.Ours([Prompt(1, "Pick channels", (2, 999))], "What do you play?", roles));
    }
}
