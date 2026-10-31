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

    [Fact]
    public void A_followed_channel_is_named_not_mentioned()
    {
        var channel = new LiveStream { TwitchId = "123", Streamer = "SomeStreamer", Platform = "Twitch", Url = "https://www.twitch.tv/somestreamer", Title = "Speedruns", StartedAt = new(2026, 10, 30, 20, 0, 0, TimeSpan.Zero) };
        Assert.Equal("🔴 **SomeStreamer** is live on Twitch: **Speedruns**\nhttps://www.twitch.tv/somestreamer", LiveWatch.Text(channel));
        channel.EndedAt = new(2026, 10, 30, 20, 45, 0, TimeSpan.Zero);
        Assert.Equal("⚫ **SomeStreamer** was live on Twitch: **Speedruns** · 45m\n-# <https://www.twitch.tv/somestreamer>", LiveWatch.Text(channel));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/SomeStreamer/", "http://twitch.tv/somestreamer")]
    [InlineData("https://m.twitch.tv/somestreamer", "https://www.twitch.tv/somestreamer")]
    public void A_stream_is_the_same_however_its_address_is_written(string status, string followed)
        => Assert.Equal(LiveWatch.Same(status), LiveWatch.Same(followed));

    [Theory]
    [InlineData("SomeStreamer", "somestreamer")]
    [InlineData("@someStreamer", "somestreamer")]
    [InlineData("https://www.twitch.tv/SomeStreamer/", "somestreamer")]
    [InlineData("twitch.tv/some_streamer", "some_streamer")]
    [InlineData("https://www.youtube.com/@someone", null)]
    [InlineData("two words", null)]
    [InlineData("ab", null)]
    public void A_twitch_channel_is_read_from_its_name_or_link(string typed, string? login)
        => Assert.Equal(login, TwitchClient.Login(typed));
}
