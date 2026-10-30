using THOBOTTO.Watch;

namespace THOBOTTO.Tests;

public class WatchRoomTests
{
    // A machine whose wall clock is corrected in steps (as WSL's is, two seconds back now and then) while its own
    // ticks run on.
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Wall { get; set; } = DateTimeOffset.UnixEpoch;

        public long Ticks { get; set; }

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => Ticks;

        public override long TimestampFrequency => 1000;

        public void Run(double seconds)
        {
            Ticks += (long)(seconds * 1000);
            Wall += TimeSpan.FromSeconds(seconds);
        }
    }

    [Fact]
    public void The_timeline_ignores_the_wall_clock_stepping_back()
    {
        var clock = new Clock();
        var room = new WatchRoom(1, 2) { AnchorSeconds = 30, AnchorAt = clock.GetTimestamp() };
        clock.Run(10);
        clock.Wall -= TimeSpan.FromSeconds(2);
        Assert.Equal(40, room.Position(clock), 3);
        room.Paused = true;
        Assert.Equal(30, room.Position(clock), 3);
    }

    [Fact]
    public void Before_the_start_the_timeline_is_a_wait()
    {
        var clock = new Clock();
        var room = new WatchRoom(1, 2) { AnchorAt = clock.GetTimestamp() + 3 * clock.TimestampFrequency };
        clock.Run(1);
        Assert.Equal(-2, room.Timeline(clock), 3);
        Assert.Equal(0, room.Position(clock), 3);
    }
}
