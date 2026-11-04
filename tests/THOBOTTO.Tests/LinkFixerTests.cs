using THOBOTTO.Links;

namespace THOBOTTO.Tests;

public class LinkFixerTests
{
    private static readonly IReadOnlyDictionary<string, string> Services = LinkFixer.Services(string.Join('\n', LinkFixer.Defaults));

    [Fact]
    public void Links_to_sites_without_previews_go_to_their_service()
    {
        var (text, count) = LinkFixer.Fix("look https://x.com/jack/status/20 and https://www.reddit.com/r/aww/comments/abc/ and https://vm.tiktok.com/ZM123/", Services);

        Assert.Equal("look https://fixupx.com/jack/status/20 and https://vxreddit.com/r/aww/comments/abc/ and https://vm.tnktok.com/ZM123/", text);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Kept_from_previewing_in_code_bare_sites_and_others_stay()
    {
        var written = "<https://x.com/a/status/1> `https://x.com/a/status/2` https://x.com https://example.com/x.com/a https://notx.com/a";

        Assert.Equal((written, 0), LinkFixer.Fix(written, Services));
    }

    [Fact]
    public void A_masked_link_and_mobile_hosts_are_fixed_too()
        => Assert.Equal("[it](https://fixupx.com/a/status/1) https://fixupx.com/b/status/2",
            LinkFixer.Fix("[it](https://mobile.twitter.com/a/status/1) https://twitter.com/b/status/2", Services).Text);

    [Fact]
    public void A_link_its_service_gives_no_preview_for_stays()
    {
        var (text, count) = LinkFixer.Fix("https://x.com/a/status/1 https://www.reddit.com/r/aww/comments/abc/", Services, fixedLink => fixedLink.Contains("fixupx"));

        Assert.Equal(("https://fixupx.com/a/status/1 https://www.reddit.com/r/aww/comments/abc/", 1), (text, count));
    }

    [Fact]
    public void Services_are_read_from_lines_and_bad_lines_skipped()
        => Assert.Equal(new Dictionary<string, string> { ["x.com"] = "fixupx.com", ["instagram.com"] = "kkinstagram.com" },
            LinkFixer.Services(" X.com = fixupx.com \n\nnonsense\ninstagram.com=kkinstagram.com\n=nothing"));

    [Fact]
    public void Message_links_in_every_form_are_found()
        => Assert.Equal(3, LinkFixer.MessageLink().Count("https://discord.com/channels/1/2/3 https://ptb.discord.com/channels/1/2/4 https://discordapp.com/channels/1/2/5"));
}
