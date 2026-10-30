using THOBOTTO.Live;

namespace THOBOTTO.Tests;

public class LiveTests
{
    private static LiveStream Stream(DateTimeOffset? ended = null) => new()
    {
        UserId = 42,
        Platform = "Twitch",
        Url = "https://www.twitch.tv/someone",
        Title = "Late night Valheim",
        Game = "Valheim",
        StartedAt = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero),
        EndedAt = ended,
    };

    [Fact]
    public void Live_ends_with_the_link_so_discord_shows_the_stream()
        => Assert.Equal("🔴 <@42> is live on Twitch: **Late night Valheim** · Valheim\nhttps://www.twitch.tv/someone", LiveWatch.Text(Stream()));

    [Fact]
    public void Over_says_how_long_and_hides_the_preview()
        => Assert.Equal("⚫ <@42> was live on Twitch: **Late night Valheim** · Valheim · 1h 30m\n-# <https://www.twitch.tv/someone>",
            LiveWatch.Text(Stream(new DateTimeOffset(2026, 10, 30, 21, 30, 0, TimeSpan.Zero))));
}
