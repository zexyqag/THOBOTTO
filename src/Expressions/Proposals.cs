using NetCord;
using NetCord.Rest;

using THOBOTTO.Happenings;

namespace THOBOTTO.Expressions;

// What every proposal (an emoji, a sticker, a sound) says and offers while it's voted on, so they all read the
// same: the status line, the buttons, the replies and the word to the proposer when it's decided.
public static class Proposals
{
    public static bool Accepted(int ups, int downs, ExpressionRules rules) => rules.DownVotes ? ups - downs >= rules.VoteMargin : ups >= rules.VoteMargin;

    public static string VotingStatus(int ups, int downs, ExpressionRules rules, DateTimeOffset endsAt)
        => rules.DownVotes
            ? $"👍 {ups} · 👎 {downs}. Needs {rules.VoteMargin} more 👍 than 👎, voting ends <t:{endsAt.ToUnixTimeSeconds()}:R>."
            : $"👍 {ups}. Needs {rules.VoteMargin} 👍, voting ends <t:{endsAt.ToUnixTimeSeconds()}:R>.";

    // "Accepted (7–2)" or "Not accepted (3–4)"; without down votes just the count of 👍.
    public static string Outcome(bool accepted, int ups, int downs, ExpressionRules rules)
        => $"{(accepted ? "Accepted" : "Not accepted")} ({(rules.DownVotes ? $"{ups}–{downs}" : $"{ups} 👍")})";

    public static ActionRowProperties Buttons(string prefix, long id, bool open, ExpressionRules rules)
    {
        var row = new ActionRowProperties { new ButtonProperties($"{prefix}:{id}:1", EmojiProperties.Standard("👍"), ButtonStyle.Success) { Disabled = !open } };
        if (rules.DownVotes)
            row.Add(new ButtonProperties($"{prefix}:{id}:0", EmojiProperties.Standard("👎"), ButtonStyle.Danger) { Disabled = !open });
        return row;
    }

    public static string VoteReply(bool up) => up ? "Voted 👍." : "Voted 👎.";

    public const string DownVotesOff = "Down votes are off here; proposals win on 👍 alone.";

    public const string OwnProposal = "You put this one up, so your vote doesn't count.";

    public const string VotingEnded = "Voting on this has ended.";

    // Where the vote is: where it was proposed, the happenings forum, or else the panel.
    public static string UpForVote(string kind, bool here, bool inForum) => (here, inForum) switch
    {
        (true, true) => $"Your {kind} is up for a vote, here and in the happenings forum.",
        (true, false) => $"Your {kind} is up for a vote here.",
        (false, true) => $"Your {kind} is up for a vote in the happenings forum.",
        _ => $"Your {kind} is up for a vote on the panel's Emoji page.",
    };

    public static string HappeningState(bool open) => open ? HappeningStates.Open : HappeningStates.Done;

    public static string Decided(string kind, string name, bool accepted, string then = "")
        => $"your {kind} `{name}` was {(accepted ? "accepted" + then : "not accepted")}";
}
