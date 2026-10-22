using THOBOTTO.Helpers;
using THOBOTTO.Panel.Pages;

namespace THOBOTTO.Tests;

public class KeptPluginTests
{
    private static readonly ProvidedPlugin Kept = new("youtube-plugin", "youtube-plugin-2be8e542d3f6f178e048dca565892684c2e40177.jar", null);

    [Fact]
    public void Nothing_to_say_when_the_folder_holds_what_runs()
        => Assert.Null(IntegrationStatus.KeptSays(Kept, "youtube-plugin", "2be8e542d3f6f178e048dca565892684c2e40177"));

    [Fact]
    public void Nothing_to_say_about_a_plugin_the_bot_doesnt_keep()
        => Assert.Null(IntegrationStatus.KeptSays(null, "lavasearch-plugin", "1.0.0"));

    [Fact]
    public void Another_version_in_the_folder_asks_for_a_restart()
    {
        var said = IntegrationStatus.KeptSays(Kept, "youtube-plugin", "1.18.2");
        Assert.NotNull(said);
        Assert.Equal(CheckState.Warning, said.State);
        Assert.Equal("Running 1.18.2; the bot keeps 2be8e54 for it. Restart Lavalink to load it.", said.Detail);
    }

    [Fact]
    public void A_jar_that_couldnt_be_kept_is_bad()
    {
        var said = IntegrationStatus.KeptSays(Kept with { Problem = "The address answered a web page instead of the file; the repository may be down." }, "youtube-plugin", "2be8e542d3f6f178e048dca565892684c2e40177");
        Assert.NotNull(said);
        Assert.Equal(CheckState.Bad, said.State);
        Assert.StartsWith("Running 2be8e54, but youtube-plugin-2be8e542d3f6f178e048dca565892684c2e40177.jar couldn't be kept for Lavalink: The address answered a web page", said.Detail);
    }
}
