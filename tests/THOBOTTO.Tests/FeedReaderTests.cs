using System.Text;

using THOBOTTO.Feeds;

namespace THOBOTTO.Tests;

public class FeedReaderTests
{
    private static FeedPage Read(string xml) => FeedReader.Read(Encoding.UTF8.GetBytes(xml))!;

    [Fact]
    public void Rss_items_have_their_guid_link_date_summary_and_image()
    {
        var page = Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Game news</title>
                <link>https://game.example/</link>
                <item>
                  <title>Patch 1.2 &amp; more</title>
                  <link>https://game.example/patch-1-2</link>
                  <guid isPermaLink="false">patch-12</guid>
                  <pubDate>Fri, 30 Oct 2026 14:05:00 +0100</pubDate>
                  <description><![CDATA[<p>Fixed the <b>bridge</b>.</p><p>New map: Fjord</p><img src="https://game.example/fjord.png">]]></description>
                </item>
                <item>
                  <title>Older</title>
                  <link>https://game.example/older</link>
                  <pubDate>Thu, 29 Oct 2026 09:00:00 EST</pubDate>
                </item>
              </channel>
            </rss>
            """);
        Assert.Equal("Game news", page.Title);
        Assert.Equal("https://game.example/", page.Link);
        var patch = page.Items[0];
        Assert.Equal(("patch-12", "Patch 1.2 & more", "https://game.example/patch-1-2"), (patch.Key, patch.Title, patch.Link));
        Assert.Equal("Fixed the bridge.\nNew map: Fjord", patch.Summary);
        Assert.Equal("https://game.example/fjord.png", patch.Image);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 5, 0, TimeSpan.Zero), patch.Published);
        // No guid: the link tells it apart; a zone by name.
        Assert.Equal("https://game.example/older", page.Items[1].Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 14, 0, 0, TimeSpan.Zero), page.Items[1].Published);
    }

    [Fact]
    public void A_youtube_channel_feed_has_its_videos_with_thumbnails()
    {
        var page = Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
              <link rel="self" href="http://www.youtube.com/feeds/videos.xml?channel_id=UCabc"/>
              <link rel="alternate" href="https://www.youtube.com/channel/UCabc"/>
              <title>A channel</title>
              <entry>
                <id>yt:video:dQw4w9WgXcQ</id>
                <title>A video</title>
                <link rel="alternate" href="https://www.youtube.com/watch?v=dQw4w9WgXcQ"/>
                <published>2026-10-30T12:00:00+00:00</published>
                <updated>2026-10-30T13:00:00+00:00</updated>
                <media:group>
                  <media:thumbnail url="https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg" width="480" height="360"/>
                  <media:description>What it's about</media:description>
                </media:group>
              </entry>
            </feed>
            """);
        Assert.Equal(("A channel", "https://www.youtube.com/channel/UCabc"), (page.Title, page.Link));
        var video = Assert.Single(page.Items);
        Assert.Equal("yt:video:dQw4w9WgXcQ", video.Key);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", video.Link);
        Assert.Equal("What it's about", video.Summary);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg", video.Image);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), video.Published);
    }

    [Fact]
    public void Rss_1_items_are_read_too()
    {
        var page = Read("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>Old style</title><link>https://old.example/</link></channel>
              <item><title>One</title><link>https://old.example/1</link><dc:date>2026-10-30T08:00:00Z</dc:date></item>
            </rdf:RDF>
            """);
        Assert.Equal("Old style", page.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), Assert.Single(page.Items).Published);
    }

    [Fact]
    public void What_isnt_a_feed_is_none()
    {
        Assert.Null(FeedReader.Read(Encoding.UTF8.GetBytes("<html><body>A page</body></html>")));
        Assert.Null(FeedReader.Read(Encoding.UTF8.GetBytes("not xml at all")));
        // No entity from outside is ever read.
        Assert.Null(FeedReader.Read(Encoding.UTF8.GetBytes("""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]><rss><channel><title>&x;</title></channel></rss>""")));
    }

    [Fact]
    public void A_long_summary_is_cut_at_a_word()
    {
        var summary = FeedReader.Summary(string.Join(' ', Enumerable.Repeat("word", 200)))!;
        Assert.True(summary.Length <= 351);
        Assert.EndsWith("word…", summary);
    }

    [Fact]
    public void A_page_names_its_own_feed()
    {
        var html = """<html><head><link rel="stylesheet" href="/x.css"><link rel="alternate" type="application/rss+xml" title="RSS" href="/feeds/videos.xml?channel_id=UCabc&amp;x=1"></head></html>""";
        Assert.Equal("https://www.youtube.com/feeds/videos.xml?channel_id=UCabc&x=1", FeedReader.Discover(html, new("https://www.youtube.com/@someone")));
        Assert.Null(FeedReader.Discover("<html><head></head></html>", new("https://a.example/")));
    }

    [Theory]
    [InlineData("https://store.steampowered.com/app/730/CounterStrike_2/", "https://store.steampowered.com/feeds/news/app/730/")]
    [InlineData("https://steamcommunity.com/app/892970", "https://store.steampowered.com/feeds/news/app/892970/")]
    [InlineData("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw", "https://www.youtube.com/feeds/videos.xml?channel_id=UCuAXFkgsw1L7xaCfnd5JJOw")]
    [InlineData(" https://blog.example/feed ", "https://blog.example/feed")]
    public void Links_people_paste_become_their_feeds(string link, string feed)
        => Assert.Equal(feed, FeedFetcher.Shortcut(link));
}
