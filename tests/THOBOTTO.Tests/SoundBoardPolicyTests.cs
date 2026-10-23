using THOBOTTO.Sounds;

namespace THOBOTTO.Tests;

public class SoundBoardPolicyTests
{
    private static BoardSound S(long id, int plays, bool onBoard = false, bool pinned = false, bool fits = true, bool wanted = true, bool trial = false)
        => new(id, pinned, onBoard, fits, wanted, plays, trial);

    [Fact]
    public void Free_slots_go_to_the_most_played_that_fit()
    {
        var plan = SoundBoardPolicy.Plan([S(1, 5), S(2, 9), S(3, 50, fits: false), S(4, 1)], freeSlots: 2, swapsLeft: 2, marginPercent: 25);
        Assert.Empty(plan.Pull);
        Assert.Equal([2L, 1L], plan.Push);
    }

    [Fact]
    public void A_clearly_more_played_sound_replaces_the_least_played_unpinned_one_within_the_days_swaps()
    {
        var sounds = new[] { S(1, 2, onBoard: true), S(2, 40, onBoard: true, pinned: true), S(3, 3, onBoard: true), S(10, 4), S(11, 9) };
        var plan = SoundBoardPolicy.Plan(sounds, freeSlots: 0, swapsLeft: 1, marginPercent: 25);
        Assert.Equal([1L], plan.Pull);
        Assert.Equal([11L], plan.Push);
    }

    [Fact]
    public void Within_the_margin_nothing_moves()
    {
        var plan = SoundBoardPolicy.Plan([S(1, 8, onBoard: true), S(2, 9)], 0, 5, 25);
        Assert.Empty(plan.Pull);
        Assert.Empty(plan.Push);
    }

    [Fact]
    public void Pinned_sounds_are_never_pulled_and_a_pinned_one_off_the_board_gets_on()
    {
        var plan = SoundBoardPolicy.Plan([S(1, 100, onBoard: true, pinned: true), S(2, 1, onBoard: true), S(3, 0, pinned: true)], 0, 1, 25);
        Assert.Equal([2L], plan.Pull);
        Assert.Equal([3L], plan.Push);
    }

    [Fact]
    public void A_sound_taken_off_discord_by_hand_isnt_put_back()
        => Assert.Empty(SoundBoardPolicy.Plan([S(1, 99, wanted: false)], 3, 3, 25).Push);

    [Fact]
    public void New_sounds_take_free_slots_first_and_are_safe_while_on_trial()
    {
        var plan = SoundBoardPolicy.Plan([S(1, 0, onBoard: true, trial: true), S(2, 7, onBoard: true), S(3, 0, trial: true), S(4, 30)], 1, 1, 25);
        Assert.Equal([3L, 4L], plan.Push);
        Assert.Equal([2L], plan.Pull);
    }

    [Fact]
    public void No_swaps_left_means_only_free_slots_are_filled()
    {
        var plan = SoundBoardPolicy.Plan([S(1, 0, onBoard: true), S(9, 50), S(8, 40)], 1, 0, 25);
        Assert.Empty(plan.Pull);
        Assert.Equal([9L], plan.Push);
    }

    [Fact]
    public void The_ranking_is_by_plays_then_pinned_then_name()
    {
        var ranked = SoundBoardPolicy.Rank(
        [
            new("boo", new(1, false, false, true, true, 3, false)),
            new("Airhorn", new(2, false, true, true, true, 9, false)),
            new("quack", new(3, true, true, true, true, 3, false)),
            new("alarm", new(4, false, false, true, true, 3, true)),
        ]);
        Assert.Equal(["Airhorn", "quack", "alarm", "boo"], ranked.Select(r => r.Name));
    }
}
