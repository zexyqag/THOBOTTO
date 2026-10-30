using THOBOTTO.Events;
using THOBOTTO.Expressions;
using THOBOTTO.Happenings;
using THOBOTTO.Mischief.Bets;
using THOBOTTO.Music.Quiz;
using THOBOTTO.Polls;
using THOBOTTO.Sounds;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Tests;

public class HappeningTests
{
    private static readonly HappeningKind[] Kinds =
    [
        EventBoard.EventHappening, EventBoard.SessionHappening, BetBook.Happening, ExpressionShelf.Happening, SoundBoard.Happening,
        WrappedPoster.Happening, ActivityPolls.Happening, QuizRooms.Happening,
    ];

    [Fact]
    public void Each_kind_goes_to_the_forum_unless_switched_off()
    {
        var rules = new HappeningRules { Off = [BetBook.Happening.Id] };
        Assert.False(rules.Shows(BetBook.Happening.Id));
        Assert.True(rules.Shows(EventBoard.EventHappening.Id));
        Assert.True(new HappeningRules().Shows(WrappedPoster.Happening.Id));
    }

    // The ids are what the posts and the settings keep.
    [Fact]
    public void The_features_kinds_keep_their_ids_and_tags_apart()
    {
        Assert.Equal(["event", "session", "bet", "emoji", "sound", "wrapped", "poll", "quiz"], Kinds.Select(k => k.Id));
        Assert.Equal(Kinds.Length, Kinds.Select(k => k.Tag).Distinct().Count());
    }

    [Fact]
    public void Done_and_cancelled_are_over()
    {
        Assert.True(HappeningStates.Over(HappeningStates.Done));
        Assert.True(HappeningStates.Over(HappeningStates.Cancelled));
        Assert.False(HappeningStates.Over(HappeningStates.Open));
    }

    [Fact]
    public void A_posted_Wrapped_is_known_by_its_period()
    {
        Assert.Equal(202610, WrappedPoster.ThingId("month:2026-10"));
        Assert.Equal(2026, WrappedPoster.ThingId("year:2026"));
        Assert.Equal("October 2026, wrapped up", WrappedPoster.Title("month:2026-10"));
        Assert.Equal("2026, wrapped", WrappedPoster.Title("year:2026"));
    }

    [Fact]
    public void Records_stay_open_a_while_and_the_rest_until_they_are_over()
    {
        Assert.Equal(TimeSpan.FromDays(7), WrappedPoster.Happening.OpenFor);
        Assert.Equal(TimeSpan.FromDays(3), ActivityPolls.Happening.OpenFor);
        Assert.Equal(TimeSpan.FromDays(3), QuizRooms.Happening.OpenFor);
        Assert.Null(EventBoard.EventHappening.OpenFor);
        Assert.Null(BetBook.Happening.OpenFor);
    }
}
