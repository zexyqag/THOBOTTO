using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ReactionChangeTests
{
    private const ulong Message = 100, Alice = 1, Bob = 2, Custom = 555;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ArchivedReaction Row(ulong user, string name, ulong? id = null, bool burst = false)
        => new() { MessageId = Message, UserId = user, EmojiId = id, EmojiName = name, Burst = burst, AddedAt = Now.AddMinutes(-5) };

    [Fact]
    public void An_add_makes_a_row_once()
    {
        var seen = new SeenReaction(Alice, null, "👍", false);
        var row = ReactionChanges.Add([], Message, seen, Now);

        Assert.NotNull(row);
        Assert.Equal(Alice, row.UserId);
        Assert.Equal("👍", row.EmojiName);
        Assert.Equal(Now, row.AddedAt);
        Assert.Null(ReactionChanges.Add([row], Message, seen, Now));
    }

    [Fact]
    public void A_super_reaction_is_its_own_row()
    {
        var normal = Row(Alice, "👍");
        var burst = ReactionChanges.Add([normal], Message, new(Alice, null, "👍", true), Now);

        Assert.NotNull(burst);
        Assert.True(burst.Burst);
    }

    [Fact]
    public void A_custom_emoji_is_known_by_its_id_whatever_its_name()
    {
        var row = Row(Alice, "oldname", Custom);

        Assert.Null(ReactionChanges.Add([row], Message, new(Alice, Custom, "newname", false), Now));
        Assert.Same(row, ReactionChanges.Remove([row], new(Alice, Custom, "newname", false)));
        Assert.True(ReactionChanges.SameEmoji(Custom, "a", Custom, "b"));
        Assert.False(ReactionChanges.SameEmoji(null, "a", Custom, "a"));
        Assert.False(ReactionChanges.SameEmoji(null, "a", null, "b"));
    }

    [Fact]
    public void A_removal_names_the_row_to_end_and_nothing_when_it_was_never_seen()
    {
        var mine = Row(Alice, "👍");
        var theirs = Row(Bob, "👍");

        Assert.Same(theirs, ReactionChanges.Remove([mine, theirs], new(Bob, null, "👍", false)));
        Assert.Null(ReactionChanges.Remove([mine], new(Bob, null, "👍", false)));
        Assert.Null(ReactionChanges.Remove([mine], new(Alice, null, "👍", true)));
    }

    [Fact]
    public void Removing_an_emoji_ends_every_row_of_it_only()
    {
        var thumbs = Row(Alice, "👍");
        var thumbsBurst = Row(Bob, "👍", burst: true);
        var heart = Row(Alice, "❤️");

        var ended = ReactionChanges.RemoveEmoji([thumbs, thumbsBurst, heart], null, "👍");

        Assert.Equal([thumbs, thumbsBurst], ended);
    }

    [Fact]
    public void Reconciling_adds_what_is_missing_and_ends_what_is_gone()
    {
        var kept = Row(Alice, "👍");
        var gone = Row(Bob, "👍");

        var (added, ended) = ReactionChanges.Reconcile([kept, gone], [new(Alice, null, "👍", false), new(Bob, Custom, "party", false)], Message, Now, askedAt: Now);

        Assert.Single(added);
        Assert.Equal((Bob, Custom), (added[0].UserId, added[0].EmojiId));
        Assert.Equal([gone], ended);
    }

    [Fact]
    public void Reconciling_with_nothing_ends_everything()
    {
        var (added, ended) = ReactionChanges.Reconcile([Row(Alice, "👍"), Row(Bob, "❤️")], [], Message, Now, askedAt: Now);

        Assert.Empty(added);
        Assert.Equal(2, ended.Count);
    }

    [Fact]
    public void A_row_added_since_the_message_was_asked_for_is_newer_than_the_answer_and_stays()
    {
        var older = Row(Alice, "👍");
        var newer = new ArchivedReaction { MessageId = Message, UserId = Bob, EmojiName = "❤️", AddedAt = Now.AddSeconds(-1) };

        var (added, ended) = ReactionChanges.Reconcile([older, newer], [], Message, Now, askedAt: Now.AddSeconds(-2));

        Assert.Empty(added);
        Assert.Equal([older], ended);
    }

    [Fact]
    public void Counts_match_when_every_emoji_adds_up_both_ways()
    {
        ArchivedReaction[] current = [Row(Alice, "👍"), Row(Bob, "👍"), Row(Alice, "party", Custom, burst: true)];

        Assert.True(ReactionChanges.CountsMatch(current, [new(null, "👍", 2, 0), new(Custom, "party", 0, 1)]));
        Assert.False(ReactionChanges.CountsMatch(current, [new(null, "👍", 3, 0), new(Custom, "party", 0, 1)]));
        Assert.False(ReactionChanges.CountsMatch(current, [new(null, "👍", 2, 0)]));
        Assert.False(ReactionChanges.CountsMatch(current, [new(null, "👍", 1, 1), new(Custom, "party", 0, 1)]));
        Assert.True(ReactionChanges.CountsMatch([], []));
        Assert.False(ReactionChanges.CountsMatch([], [new(null, "👍", 1, 0)]));
    }
}

public class VoteChangeTests
{
    private const ulong Message = 100, Alice = 1, Bob = 2;

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static ArchivedPollVote Row(ulong user, int answer) => new() { MessageId = Message, UserId = user, AnswerId = answer, AddedAt = Now.AddMinutes(-5) };

    [Fact]
    public void A_vote_is_recorded_once_and_withdrawn_by_its_row()
    {
        var vote = VoteChanges.Add([], Message, new(Alice, 1), Now);

        Assert.NotNull(vote);
        Assert.Null(VoteChanges.Add([vote], Message, new(Alice, 1), Now));
        Assert.NotNull(VoteChanges.Add([vote], Message, new(Alice, 2), Now));
        Assert.Same(vote, VoteChanges.Remove([vote], new(Alice, 1)));
        Assert.Null(VoteChanges.Remove([vote], new(Bob, 1)));
    }

    [Fact]
    public void Reconciling_follows_the_voters_listed()
    {
        var kept = Row(Alice, 1);
        var gone = Row(Bob, 1);

        var (added, ended) = VoteChanges.Reconcile([kept, gone], [new(Alice, 1), new(Bob, 2)], Message, Now, askedAt: Now);

        Assert.Single(added);
        Assert.Equal((Bob, 2), (added[0].UserId, added[0].AnswerId));
        Assert.Equal([gone], ended);
    }

    [Fact]
    public void Counts_match_when_the_results_add_up()
    {
        ArchivedPollVote[] current = [Row(Alice, 1), Row(Bob, 1), Row(Alice, 2)];

        Assert.True(VoteChanges.CountsMatch(current, [(1, 2), (2, 1)]));
        Assert.False(VoteChanges.CountsMatch(current, [(1, 2)]));
        Assert.False(VoteChanges.CountsMatch(current, [(1, 1), (2, 1)]));
        Assert.True(VoteChanges.CountsMatch([], []));
    }
}
