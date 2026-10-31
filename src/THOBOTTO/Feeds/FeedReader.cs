using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace THOBOTTO.Feeds;

// One entry of a feed. Key is what tells it from the others: its id, else its link, else its title.
public sealed record FeedItem(string Key, string Title, string? Link, string? Summary, string? Image, DateTimeOffset? Published);

public sealed record FeedPage(string Title, string? Link, IReadOnlyList<FeedItem> Items);

// Reads RSS (0.9x, 1.0, 2.0) and Atom by the elements' local names, so the namespaces each flavour uses don't matter.
public static partial class FeedReader
{
    private const int SummaryLength = 350;

    private static readonly HtmlParser Html = new();

    // Elements that end a line.
    private static readonly HashSet<string> Blocks = ["p", "div", "li", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "blockquote", "pre", "ul", "ol", "table", "section", "article"];

    // From the bytes, so the document's own encoding is kept; null when it isn't a feed.
    public static FeedPage? Read(byte[] bytes)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        var root = document.Root!;
        if (root.Name.LocalName == "feed")
            return Atom(root);
        if (root.Name.LocalName is not ("rss" or "RDF"))
            return null;
        var channel = Child(root, "channel") ?? root;
        var items = root.Descendants().Where(e => e.Name.LocalName == "item").Select(RssItem).OfType<FeedItem>().ToList();
        return new(Text(Child(channel, "title")) ?? "A feed", Text(Child(channel, "link")), items);
    }

    private static FeedPage Atom(XElement feed)
    {
        var items = feed.Elements().Where(e => e.Name.LocalName == "entry").Select(entry =>
        {
            var link = AtomLink(entry);
            var title = Text(Child(entry, "title"));
            var key = Text(Child(entry, "id")) ?? link ?? title;
            if (key is null)
                return null;
            var media = Child(entry, "group");
            var summary = Text(Child(entry, "summary")) ?? Text(Child(media, "description")) ?? Text(Child(entry, "content"));
            var image = Child(media, "thumbnail")?.Attribute("url")?.Value ?? FirstImage(Text(Child(entry, "content")) ?? summary);
            return new FeedItem(key, Plain(title) ?? "Untitled", link, Summary(summary), image, Date(Child(entry, "published")) ?? Date(Child(entry, "updated")));
        }).OfType<FeedItem>().ToList();
        return new(Plain(Text(Child(feed, "title"))) ?? "A feed", AtomLink(feed), items);
    }

    private static FeedItem? RssItem(XElement item)
    {
        var link = Text(Child(item, "link"));
        var title = Text(Child(item, "title"));
        var key = Text(Child(item, "guid")) ?? link ?? title;
        if (key is null)
            return null;
        var description = Text(Child(item, "description"));
        var content = Text(Child(item, "encoded"));
        var image = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure" && (e.Attribute("type")?.Value.StartsWith("image/") ?? false))?.Attribute("url")?.Value
            ?? item.Elements().FirstOrDefault(e => e.Name.LocalName is "thumbnail" or "content" && e.Attribute("url") is not null && e.Attribute("medium")?.Value is null or "image")?.Attribute("url")?.Value
            ?? FirstImage(content ?? description);
        var published = Date(Child(item, "pubDate")) ?? Date(Child(item, "date"));
        return new(key, Plain(title) ?? "Untitled", link, Summary(description ?? content), image, published);
    }

    // An HTML page's own feed, as its head names it: a YouTube channel's, a blog's.
    public static string? Discover(string html, Uri page)
    {
        foreach (var link in Html.ParseDocument(html).QuerySelectorAll("link[href]"))
        {
            if (link.GetAttribute("rel")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase) == true
                && link.GetAttribute("type")?.Trim().ToLowerInvariant() is "application/rss+xml" or "application/atom+xml"
                && Uri.TryCreate(page, link.GetAttribute("href"), out var feed) && feed.Scheme is "http" or "https")
                return feed.AbsoluteUri;
        }
        return null;
    }

    // Plain text from a summary that may be HTML, cut at a word near the length.
    public static string? Summary(string? html)
    {
        if (Plain(html) is not { } text)
            return null;
        if (text.Length <= SummaryLength)
            return text;
        var cut = text.LastIndexOf(' ', SummaryLength);
        return text[..(cut > SummaryLength / 2 ? cut : SummaryLength)].TrimEnd(',', '.', ';', ':', ' ') + "…";
    }

    // The words, a line for each block and break; scripts and styles left out.
    private static string? Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var body = Html.ParseDocument(html).Body!;
        foreach (var hidden in body.QuerySelectorAll("script, style").ToList())
            hidden.Remove();
        var words = new StringBuilder();
        Walk(body, words);
        var text = string.Join('\n', words.ToString().Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));
        return text.Length > 0 ? text : null;
    }

    private static void Walk(INode node, StringBuilder words)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
                words.Append(text.Data);
            else if (child is IElement element)
            {
                if (element.LocalName == "br")
                    words.Append('\n');
                Walk(element, words);
                if (Blocks.Contains(element.LocalName))
                    words.Append('\n');
            }
        }
    }

    private static string? FirstImage(string? html)
        => html is not null && Html.ParseDocument(html).QuerySelector("img[src]")?.GetAttribute("src") is { } src && src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : null;

    private static string? AtomLink(XElement parent)
        => parent.Elements().Where(e => e.Name.LocalName == "link")
            .OrderBy(e => e.Attribute("rel")?.Value is null or "alternate" ? 0 : 1)
            .Select(e => e.Attribute("href")?.Value).FirstOrDefault(h => h is not null);

    private static DateTimeOffset? Date(XElement? element)
    {
        if (Text(element) is not { } text)
            return null;
        // RFC 822 as feeds write it: the zone a name or an offset without a colon, the day's name sometimes wrong.
        text = Zone().Replace(text, m => m.Value switch
        {
            "UT" or "GMT" or "Z" => "+00:00",
            "EST" => "-05:00",
            "EDT" => "-04:00",
            "CST" => "-06:00",
            "CDT" => "-05:00",
            "MST" => "-07:00",
            "MDT" => "-06:00",
            "PST" => "-08:00",
            "PDT" => "-07:00",
            _ => $"{m.Value[..3]}:{m.Value[3..]}",
        });
        text = DayName().Replace(text, "");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null;
    }

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement? element) => element?.Value.Trim() is { Length: > 0 } text ? text : null;

    [GeneratedRegex(@"[ \t\r\u00a0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<=\d\d:\d\d(:\d\d)?\s*)(UT|GMT|Z|[ECMP][SD]T|[+-]\d{4})$")]
    private static partial Regex Zone();

    [GeneratedRegex(@"^[A-Za-z]{3},\s*")]
    private static partial Regex DayName();
}
