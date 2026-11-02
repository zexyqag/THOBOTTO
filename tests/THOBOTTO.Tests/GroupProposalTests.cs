using NetCord;
using NetCord.Rest;

using THOBOTTO.Events;
using THOBOTTO.Games;

namespace THOBOTTO.Tests;

public class GroupProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset[] Times = [Now.AddDays(1), Now.AddDays(2), Now.AddDays(3)];

    private static GroupProposalVote Vote(ulong user, int choice, string name = "") => new() { ProposalId = 1, UserId = user, Choice = choice, Name = name };

    [Fact]
    public void The_time_most_can_make() => Assert.Equal(1, GroupProposals.Favourite(Times, [Vote(1, 0), Vote(1, 1), Vote(2, 1), Vote(3, GroupProposals.CantMake)], Now));

    [Fact]
    public void Of_a_tie_the_earliest() => Assert.Equal(0, GroupProposals.Favourite(Times, [Vote(1, 2), Vote(2, 0)], Now));

    [Fact]
    public void Without_votes_the_earliest() => Assert.Equal(0, GroupProposals.Favourite(Times, [], Now));

    [Fact]
    public void Never_a_time_that_passed()
    {
        Assert.Equal(2, GroupProposals.Favourite(Times, [Vote(1, 0), Vote(2, 0), Vote(1, 2)], Now.AddDays(1.5)));
        Assert.Null(GroupProposals.Favourite(Times, [Vote(1, 0)], Now.AddDays(4)));
    }

    private static readonly Game Valheim = new() { Id = 2, GuildId = 1, Name = "Valheim", Emoji = "🪓" };
    private static readonly Game Lethal = new() { Id = 5, GuildId = 1, Name = "Lethal Company" };

    private static GroupProposal Proposal(params DateTimeOffset[] times)
        => new() { Id = 7, GuildId = 1, GameIds = [2], ProposerId = 3, ProposerName = "Sam", Times = times };

    private static GroupProposalGameVote Pick(ulong user, long game, string name = "") => new() { ProposalId = 7, UserId = user, GameId = game, Name = name };

    [Fact]
    public void One_time_asks_whos_in()
    {
        var view = GroupProposals.View(Proposal(Times[0]), [Vote(1, 0, "Ana"), Vote(2, GroupProposals.CantMake, "Bo")], [], "Example Server", [Valheim], enough: 4);

        Assert.Contains("**Valheim session** in **Example Server**, proposed by Sam", view.Content);
        Assert.Contains($"<t:{Times[0].ToUnixTimeSeconds()}:F>", view.Content);
        Assert.Contains(": Ana", view.Content);
        Assert.Contains("Can't: Bo", view.Content);
        var buttons = Assert.Single(view.Rows).Components.Cast<ButtonProperties>().ToList();
        Assert.Equal(["groupvote:7:0", "groupvote:7:-1", "groupsuggest:7", "groupplan:7"], buttons.Select(b => b.CustomId));
        Assert.Equal("I'm in", buttons[0].Label);
    }

    [Fact]
    public void Several_times_get_a_numbered_button_each()
    {
        var times = Enumerable.Range(1, 10).Select(d => Now.AddDays(d)).ToArray();
        var view = GroupProposals.View(Proposal(times), [Vote(1, 9, "Ana")], [], "Example Server", [Valheim], enough: 4);

        Assert.Contains("\U0001F51F", view.Content);
        Assert.Contains("nobody yet", view.Content);
        Assert.DoesNotContain("Can't:", view.Content, StringComparison.Ordinal);
        var buttons = view.Rows.SelectMany(r => r.Components.Cast<ButtonProperties>()).ToList();
        Assert.Equal(12, buttons.Count);
        Assert.DoesNotContain(buttons, b => b.CustomId == "groupsuggest:7");
        Assert.All(view.Rows, r => Assert.True(r.Components.Count() <= 5));
        Assert.Equal("1\u20E3", buttons[0].Emoji!.Name);
        Assert.Equal("groupplan:7", buttons[^1].CustomId);
    }

    [Fact]
    public void A_title_of_its_own_names_the_game()
    {
        var proposal = new GroupProposal { Id = 7, GuildId = 1, GameIds = [2], ProposerName = "Sam", Title = "Viking night", Times = [Times[0]] };
        Assert.StartsWith("📅 **Viking night** (🪓 Valheim) in", GroupProposals.View(proposal, [], [], "Example Server", [Valheim], enough: 4).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_games_are_voted_on_with_a_row_of_their_own()
    {
        var proposal = new GroupProposal { Id = 7, GuildId = 1, GameIds = [2, 5], ProposerName = "Sam", Times = [Times[0], Times[1]] };
        var view = GroupProposals.View(proposal, [Vote(1, 0, "Ana")], [Pick(1, 5, "Ana"), Pick(2, 5, "Bo")], "Example Server", [Valheim, Lethal], enough: 4);

        Assert.Contains("**Valheim or Lethal Company session** in **Example Server**", view.Content);
        Assert.Contains("🪓 Valheim: nobody yet", view.Content);
        Assert.Contains("Lethal Company: Ana, Bo", view.Content);
        Assert.Contains("Press the times you can make and the games you'd play", view.Content);
        var rows = view.Rows.Select(r => r.Components.Cast<ButtonProperties>().Select(b => b.CustomId).ToList()).ToList();
        Assert.Equal([["groupvote:7:0", "groupvote:7:1"], ["groupgame:7:2", "groupgame:7:5"], ["groupvote:7:-1", "groupsuggest:7", "groupplan:7"]], rows);
        Assert.Equal("🪓", view.Rows[1].Components.Cast<ButtonProperties>().First().Emoji!.Name);
    }

    [Fact]
    public void The_game_most_would_play_and_of_a_tie_the_first()
    {
        Assert.Equal(5, GroupProposals.FavouriteGame([2, 5], [Pick(1, 5), Pick(2, 5), Pick(3, 2)]));
        Assert.Equal(2, GroupProposals.FavouriteGame([2, 5], [Pick(1, 5), Pick(3, 2)]));
        Assert.Equal(2, GroupProposals.FavouriteGame([2, 5], []));
        Assert.Null(GroupProposals.FavouriteGame([], []));
    }

    private static PlannedSession Session(string? state, IReadOnlyList<PlannedSuggestion>? suggestions = null)
        => new("Valheim session", "Example Server", state, Times[0], "https://discord.com/channels/1/2/3", ["Ana"], ["Bo"], ["Cy"],
            suggestions ?? [], "It moves when 2 of the 2 who are in say it works.", BeforeStart: true);

    [Fact]
    public void Planned_the_group_can_still_sign_up_and_see_how_it_stands()
    {
        var view = GroupProposals.Planned(7, Session(EventStates.Scheduled));

        Assert.Contains("✅ **Valheim session** is planned in **Example Server**", view.Content);
        Assert.Contains("[the session](https://discord.com/channels/1/2/3)", view.Content);
        Assert.Contains("In: Ana", view.Content);
        Assert.Contains("Waiting for a spot: Bo", view.Content);
        Assert.Contains("Not in the server: Cy", view.Content);
        Assert.Equal([["groupgo:7:in", "groupgo:7:out", "groupgo:7:refresh"], ["groupsuggestmove:7"]], view.Rows.Select(r => r.Components.Cast<ButtonProperties>().Select(b => b.CustomId).ToList()));
    }

    [Fact]
    public void Planned_suggestions_can_be_agreed_to_or_moved_to_from_the_group()
    {
        var view = GroupProposals.Planned(7, Session(EventStates.Scheduled, [new(40, Times[1], "Bo", ["Bo", "Ana"])]));

        Assert.Contains("💡 **1.**", view.Content);
        Assert.Contains("suggested by Bo · works for Bo, Ana", view.Content);
        Assert.Contains("It moves when 2 of the 2 who are in say it works.", view.Content);
        Assert.Equal(["groupworks:7:40", "groupmoveto:7:40"], view.Rows[^1].Components.Cast<ButtonProperties>().Select(b => b.CustomId));
    }

    [Theory]
    [InlineData(EventStates.Cancelled, "was cancelled")]
    [InlineData(EventStates.Over, "is over")]
    [InlineData(null, "is no longer planned")]
    public void A_session_cancelled_over_or_gone_is_said_without_buttons(string? state, string said)
    {
        var view = GroupProposals.Planned(7, Session(state));
        Assert.Contains(said, view.Content);
        Assert.Empty(view.Rows);
    }

    [Theory]
    [InlineData(Deciding.AsSoonAsEnough, "as soon as a time has 4, else by the most votes")]
    [InlineData(Deciding.WhenItCloses, "planned in the server by the most votes")]
    [InlineData(Deciding.PlannerDecides, "Sam plans it in the server with Plan it.")]
    public void It_says_how_it_will_be_settled(string rule, string said)
    {
        var proposal = new GroupProposal { Id = 7, GuildId = 1, GameIds = [2], ProposerName = "Sam", Times = [Times[0]], Decide = rule, ClosesAt = Now.AddDays(1) };
        Assert.Contains(said, GroupProposals.View(proposal, [], [], "Example Server", [Valheim], enough: 4).Content, StringComparison.Ordinal);
    }
}
