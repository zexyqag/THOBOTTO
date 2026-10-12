using THOBOTTO.Integrations;

namespace THOBOTTO.Tests;

public class BotVersionTests
{
    [Fact]
    public void A_release_parses_to_its_numbers()
        => Assert.Equal((0, 1, 0, "", ""), BotVersion.Parse("0.1.0"));

    [Fact]
    public void A_branch_build_keeps_its_prerelease()
        => Assert.Equal((0, 1, 0, "main.abc1234", ""), BotVersion.Parse("0.1.0-main.abc1234"));

    [Fact]
    public void A_build_from_source_keeps_its_commit()
        => Assert.Equal((0, 0, 0, "", "abc1234"), BotVersion.Parse("0.0.0+abc1234"));

    [Theory]
    [InlineData("latest")]
    [InlineData("0.1")]
    [InlineData("0.1.x")]
    [InlineData("")]
    public void Anything_else_does_not_parse(string text)
        => Assert.Null(BotVersion.Parse(text));

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.1.0-main.abc1234", false)]
    [InlineData("0.0.0+abc1234", false)]
    [InlineData("0.0.0", false)]
    [InlineData("latest", false)]
    public void Only_a_numbered_build_is_a_release(string version, bool release)
        => Assert.Equal(release, BotVersion.IsRelease(version));

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1.0", "0.1.0-main.abc1234", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.1.0-main.abc1234", "0.1.0", false)]
    [InlineData("latest", "0.1.0", false)]
    public void Newer_goes_by_the_numbers(string version, string than, bool newer)
        => Assert.Equal(newer, BotVersion.IsNewer(version, than));

    [Theory]
    [InlineData("0.0.0+0123456789abcdef0123456789abcdef01234567", "0.0.0+0123456")]
    [InlineData("0.0.0+abc1234", "0.0.0+abc1234")]
    [InlineData("0.1.0", "0.1.0")]
    public void The_commit_is_shortened_for_display(string version, string shown)
        => Assert.Equal(shown, BotVersion.Shorten(version));

    [Fact]
    public void The_running_build_has_a_version()
        => Assert.NotNull(BotVersion.Parse(BotVersion.Running));

    [Fact]
    public void The_latest_release_is_good()
    {
        var check = IntegrationStatus.RunningBuild("0.1.0", "0.1.0");
        Assert.Equal(CheckState.Good, check.State);
        Assert.Equal("0.1.0, the latest release.", check.Detail);
    }

    [Fact]
    public void An_older_release_is_told_the_newer_one()
    {
        var check = IntegrationStatus.RunningBuild("0.1.0", "0.2.0");
        Assert.Equal(CheckState.Warning, check.State);
        Assert.Equal("0.1.0; 0.2.0 is out.", check.Detail);
    }

    [Fact]
    public void A_branch_build_is_not_compared()
    {
        var check = IntegrationStatus.RunningBuild("0.0.0-main.abc1234", "0.1.0");
        Assert.Equal(CheckState.Good, check.State);
        Assert.Equal("0.0.0-main.abc1234: not a release; the latest is 0.1.0.", check.Detail);
    }

    [Fact]
    public void A_release_with_none_published_is_good()
    {
        var check = IntegrationStatus.RunningBuild("0.1.0", null);
        Assert.Equal(CheckState.Good, check.State);
        Assert.Equal("0.1.0; no release is published yet.", check.Detail);
    }

    [Fact]
    public void A_branch_build_with_none_published_says_so()
    {
        var check = IntegrationStatus.RunningBuild("0.0.0-main.abc1234", null);
        Assert.Equal(CheckState.Good, check.State);
        Assert.Equal("0.0.0-main.abc1234: not a release; none is published yet.", check.Detail);
    }
}
