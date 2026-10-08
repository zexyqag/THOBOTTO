using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class DriftTests
{
    private const ulong Mirror = 1;

    // A track sent at 0, the leader playing from 100 ms on and reporting every 5 s.
    private static DriftTracker Playing()
    {
        var drift = new DriftTracker();
        drift.Start(0);
        drift.Leader(5_000, 4_900);
        drift.Leader(10_000, 9_900);
        return drift;
    }

    [Fact]
    public void A_mirror_in_step_is_left_alone()
    {
        var drift = Playing();
        Assert.Null(drift.Mirror(Mirror, 12_000, 11_850));
        Assert.Null(drift.Mirror(Mirror, 17_000, 16_850));
    }

    [Fact]
    public void A_mirror_out_of_step_twice_running_seeks_to_the_leader()
    {
        var drift = Playing();
        Assert.Null(drift.Mirror(Mirror, 12_000, 11_000));
        Assert.Equal(-900, drift.Mirror(Mirror, 17_000, 16_000));
        Assert.Equal(20_900, drift.Position(21_000));
    }

    [Fact]
    public void One_bad_mirror_report_is_ignored()
    {
        var drift = Playing();
        Assert.Null(drift.Mirror(Mirror, 12_000, 11_000));
        Assert.Null(drift.Mirror(Mirror, 17_000, 16_900));
        Assert.Null(drift.Mirror(Mirror, 22_000, 21_000));
    }

    [Fact]
    public void One_bad_leader_report_doesnt_move_the_leader()
    {
        // As seen live: an off-schedule leader report 900 ms ahead.
        var drift = Playing();
        drift.Leader(12_000, 12_800);
        Assert.Null(drift.Mirror(Mirror, 14_000, 13_900));
        Assert.Null(drift.Mirror(Mirror, 19_000, 18_900));
    }

    [Fact]
    public void Mirrors_arent_judged_before_the_leader_reports()
    {
        var drift = new DriftTracker();
        drift.Start(0);
        Assert.Null(drift.Mirror(Mirror, 5_000, 1_000));
        Assert.Null(drift.Mirror(Mirror, 10_000, 6_000));
    }

    [Fact]
    public void Reports_from_the_previous_track_are_ignored()
    {
        var drift = Playing();
        drift.Start(20_000);
        drift.Leader(19_999, 19_899);
        Assert.Equal(1_000, drift.Position(21_000));
    }

    [Fact]
    public void Paused_holds_the_position_and_resume_carries_on_from_it()
    {
        var drift = Playing();
        drift.Pause(12_000);
        Assert.Equal(11_900, drift.Position(30_000));
        Assert.Null(drift.Mirror(Mirror, 31_000, 1_000));

        drift.Resume(40_000);
        Assert.Equal(12_900, drift.Position(41_000));
    }
}
