using THOBOTTO.Archive;

namespace THOBOTTO.Tests;

public class ArchiveFileTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("audio/mpeg")]
    [InlineData("audio/ogg")]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    public void Pictures_sound_and_video_show_inline(string type) => Assert.True(ArchiveFiles.Inline(type));

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/pdf")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_a_browser_could_run_is_a_download(string? type) => Assert.False(ArchiveFiles.Inline(type));

    [Fact]
    public void A_type_is_read_without_its_parameters_or_casing()
    {
        Assert.True(ArchiveFiles.Inline("Image/PNG"));
        Assert.True(ArchiveFiles.Inline("video/mp4; codecs=\"avc1\""));
        Assert.False(ArchiveFiles.Inline("; image/png"));
    }
}
