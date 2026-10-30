using THOBOTTO.Expressions;

namespace THOBOTTO.Tests;

public class ProposalsTests
{
    private static readonly DateTimeOffset Ends = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_down_votes_the_lead_counts_and_both_buttons_show()
    {
        var rules = new ExpressionRules { VoteMargin = 3 };
        Assert.True(Proposals.Accepted(5, 2, rules));
        Assert.False(Proposals.Accepted(5, 3, rules));
        Assert.Equal($"👍 5 · 👎 2. Needs 3 more 👍 than 👎, voting ends <t:{Ends.ToUnixTimeSeconds()}:R>.", Proposals.VotingStatus(5, 2, rules, Ends));
        Assert.Equal("Accepted (5–2)", Proposals.Outcome(true, 5, 2, rules));
        Assert.Equal(2, Proposals.Buttons("x", 1, true, rules).Count());
    }

    [Fact]
    public void Without_down_votes_the_ups_count_and_only_one_button_shows()
    {
        var rules = new ExpressionRules { VoteMargin = 3, DownVotes = false };
        Assert.True(Proposals.Accepted(3, 9, rules));
        Assert.False(Proposals.Accepted(2, 0, rules));
        Assert.Equal($"👍 2. Needs 3 👍, voting ends <t:{Ends.ToUnixTimeSeconds()}:R>.", Proposals.VotingStatus(2, 0, rules, Ends));
        Assert.Equal("Not accepted (2 👍)", Proposals.Outcome(false, 2, 0, rules));
        Assert.Single(Proposals.Buttons("x", 1, true, rules));
    }

    [Fact]
    public void The_proposer_hears_the_same_words_for_every_kind()
    {
        Assert.Equal("your sticker `wow` was accepted and is live", Proposals.Decided("sticker", "wow", true, " and is live"));
        Assert.Equal("your sound `boo` was not accepted", Proposals.Decided("sound", "boo", false, ": it's in the library"));
        Assert.Equal("Your emoji is up for a vote, here and in the happenings forum.", Proposals.UpForVote("emoji", here: true, inForum: true));
        Assert.Equal("Your sound is up for a vote here.", Proposals.UpForVote("sound", here: true, inForum: false));
        Assert.Equal("Your sticker is up for a vote on the panel's Emoji page.", Proposals.UpForVote("sticker", here: false, inForum: false));
    }
}
