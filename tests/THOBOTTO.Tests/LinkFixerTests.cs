using THOBOTTO.Links;

namespace THOBOTTO.Tests;

public class LinkFixerTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Services = LinkFixer.Services(string.Join('\n', LinkFixer.Defaults));

    [Fact]
    public void Each_link_to_such_a_site_comes_with_its_services_in_turn()
    {
        var links = LinkFixer.Links("look https://x.com/jack/status/20 and https://www.reddit.com/r/aww/comments/abc/ and https://vm.tiktok.com/ZM123/", Services);

        Assert.Equal(["https://fixupx.com/jack/status/20", "https://vxtwitter.com/jack/status/20"], links[0].Swaps);
        Assert.Equal(("reddit.com", "https://vxreddit.com/r/aww/comments/abc/"), (links[1].Site, links[1].Swaps[0]));
        Assert.Equal(["https://vm.tnktok.com/ZM123/", "https://vm.tiktxk.com/ZM123/"], links[2].Swaps);
    }

    [Fact]
    public void A_site_with_no_services_comes_with_none_for_the_bot_to_fetch()
        => Assert.Empty(LinkFixer.Links("https://www.instagram.com/reel/abc/", LinkFixer.Services("instagram.com=")).Single().Swaps);

    [Fact]
    public void Kept_from_previewing_in_code_bare_sites_and_others_are_no_such_links()
        => Assert.Empty(LinkFixer.Links("<https://x.com/a/status/1> `https://x.com/a/status/2` https://x.com https://example.com/x.com/a https://notx.com/a", Services));

    [Fact]
    public void Short_links_are_found_on_their_hosts_only()
        => Assert.Equal(["https://v.redd.it/abc", "https://www.redd.it/def"],
            LinkFixer.Short("https://v.redd.it/abc <https://v.redd.it/kept> `https://redd.it/code` https://www.redd.it/def https://i.redd.it/x.jpg https://www.reddit.com/r/a/comments/b/", ["v.redd.it", "redd.it"]));

    [Fact]
    public void A_masked_link_and_mobile_hosts_are_found_too()
        => Assert.Equal(["https://fixupx.com/a/status/1", "https://fixupx.com/b/status/2"],
            LinkFixer.Links("[it](https://mobile.twitter.com/a/status/1) https://twitter.com/b/status/2", Services).Select(l => l.Swaps[0]));

    [Fact]
    public void Links_given_a_replacement_are_replaced_and_the_rest_stay()
        => Assert.Equal("a https://fixupx.com/a/status/1 b <https://www.tiktok.com/@x/video/1> c https://reddit.com/r/x/comments/y/",
            LinkFixer.Replace("a https://x.com/a/status/1 b https://www.tiktok.com/@x/video/1 c https://reddit.com/r/x/comments/y/",
                new Dictionary<string, string> { ["https://x.com/a/status/1"] = "https://fixupx.com/a/status/1", ["https://www.tiktok.com/@x/video/1"] = "<https://www.tiktok.com/@x/video/1>" }));

    [Fact]
    public void Services_are_read_from_lines_in_order_and_bad_lines_skipped()
    {
        var read = LinkFixer.Services(" X.com = fixupx.com, VXtwitter.com \n\nnonsense\ninstagram.com=kkinstagram.com\n=nothing\nempty.com=");

        Assert.Equal(["fixupx.com", "vxtwitter.com"], read["x.com"]);
        Assert.Equal(["kkinstagram.com"], read["instagram.com"]);
        Assert.Empty(read["empty.com"]);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void Message_links_in_every_form_are_found()
        => Assert.Equal(3, LinkFixer.MessageLink().Count("https://discord.com/channels/1/2/3 https://ptb.discord.com/channels/1/2/4 https://discordapp.com/channels/1/2/5"));

    [Fact]
    public void Cookies_come_from_a_cookies_txt_or_a_copied_curl()
    {
        var (file, _) = LinkCookies.Read("# Netscape HTTP Cookie File\n.instagram.com\tTRUE\t/\tTRUE\t0\tsessionid\tabc\n.reddit.com\tTRUE\t/\tTRUE\t0\treddit_session\tdef\n");
        Assert.Equal(["instagram.com", "reddit.com"], LinkCookies.Sites(file!));

        var (curl, _) = LinkCookies.Read("curl 'https://www.instagram.com/p/x/' -H 'accept: */*' -b 'sessionid=abc; csrftoken=def'");
        Assert.Contains(".instagram.com\tTRUE\t/\tTRUE\t", curl);
        Assert.Contains("\tsessionid\tabc\n", curl);

        Assert.NotNull(LinkCookies.Read("just words").Problem);
    }
}
