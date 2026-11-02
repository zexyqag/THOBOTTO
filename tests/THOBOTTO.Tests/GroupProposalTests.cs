using NetCord;
using NetCord.Rest;

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

    private static GroupProposal Proposal(params DateTimeOffset[] times)
        => new() { Id = 7, GuildId = 1, GameId = 2, ProposerId = 3, ProposerName = "Sam", Title = "Valheim session", Times = times };

    [Fact]
    public void One_time_asks_whos_in()
    {
        var view = GroupProposals.View(Proposal(Times[0]), [Vote(1, 0, "Ana"), Vote(2, GroupProposals.CantMake, "Bo")], "Example Server", null);

        Assert.Contains("**Valheim session** in **Example Server**, proposed by Sam", view.Content);
        Assert.Contains($"<t:{Times[0].ToUnixTimeSeconds()}:F>", view.Content);
        Assert.Contains(": Ana", view.Content);
        Assert.Contains("Can't: Bo", view.Content);
        var buttons = Assert.Single(view.Rows).Components.Cast<ButtonProperties>().ToList();
        Assert.Equal(["groupvote:7:0", "groupvote:7:-1", "groupplan:7"], buttons.Select(b => b.CustomId));
        Assert.Equal("I'm in", buttons[0].Label);
    }

    [Fact]
    public void Several_times_get_a_numbered_button_each()
    {
        var times = Enumerable.Range(1, 10).Select(d => Now.AddDays(d)).ToArray();
        var view = GroupProposals.View(Proposal(times), [Vote(1, 9, "Ana")], "Example Server", null);

        Assert.Contains("\U0001F51F", view.Content);
        Assert.Contains("nobody yet", view.Content);
        Assert.DoesNotContain("Can't:", view.Content, StringComparison.Ordinal);
        var buttons = view.Rows.SelectMany(r => r.Components.Cast<ButtonProperties>()).ToList();
        Assert.Equal(12, buttons.Count);
        Assert.All(view.Rows, r => Assert.True(r.Components.Count() <= 5));
        Assert.Equal("1\u20E3", buttons[0].Emoji!.Name);
        Assert.Equal("groupplan:7", buttons[^1].CustomId);
    }
}
