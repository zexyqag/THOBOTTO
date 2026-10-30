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

    // ffmpeg lists a segment once it's written, and ends the list when the video is all made.
    [Fact]
    public void A_stream_is_as_far_ahead_as_its_playlist_lists()
    {
        const string making = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:4\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXT-X-MAP:URI=\"init.mp4\"\n"
            + "#EXTINF:4.004000,\nseg00000.m4s\n#EXTINF:3.996000,\nseg00001.m4s\n";
        var (seconds, whole) = HlsStreams.Playlist(making);
        Assert.Equal(8, seconds, 3);
        Assert.False(whole);
        Assert.True(HlsStreams.Playlist(making + "#EXT-X-ENDLIST\n").Whole);
        Assert.Equal(0, HlsStreams.Playlist("#EXTM3U\n").Seconds);
    }
}
