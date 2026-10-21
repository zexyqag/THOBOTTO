using THOBOTTO.Panel;

namespace THOBOTTO.Tests;

public class PanelPathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/servers/1", "/servers/1")]
    [InlineData("/me?tab=data", "/me?tab=data")]
    [InlineData("//evil.example/", null)]
    [InlineData("/\\evil.example/", null)]
    [InlineData("https://evil.example/", null)]
    [InlineData("servers/1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_the_panels_own_paths(string? path, string? expected) => Assert.Equal(expected, PanelPath.Local(path));
}
