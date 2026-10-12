using THOBOTTO.Music;

namespace THOBOTTO.Tests;

public class LyricsTests
{
    [Fact]
    public void Synced_lines_come_in_time_order()
        => Assert.Equal(
            [new LyricLine(1_500, "one"), new LyricLine(62_000, "two"), new LyricLine(90_250, "three")],
            LyricsFinder.SyncedLines("[01:30.25] three\n[00:01.50] one\n[01:02.00] two"));

    [Fact]
    public void Unsynced_lyrics_have_no_lines()
        => Assert.Empty(LyricsFinder.SyncedLines(""));
}
