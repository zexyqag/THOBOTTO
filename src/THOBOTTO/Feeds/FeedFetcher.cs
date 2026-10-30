using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using THOBOTTO.GameServers;

namespace THOBOTTO.Feeds;

// A feed read: its page, or that it hasn't changed since the last read, or why it couldn't be read.
public sealed record FeedRead(FeedPage? Page, string? ETag, string? LastModified, string? Problem)
{
    public bool Unchanged => Page is null && Problem is null;
}

// Reads feeds over the internet, never inside the bot's own network (members with the permission choose the links).
public sealed partial class FeedFetcher([FromKeyedServices(nameof(FeedFetcher))] HttpClient http)
{
    private const int MaxBytes = 5 * 1024 * 1024;

    // The handler for its HttpClient: every connection, a redirect's too, goes only to public addresses.
    public static SocketsHttpHandler Handler() => new()
    {
        UseProxy = false,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || !addresses.All(PublicAddress.IsPublic))
                throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't out on the internet.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    // A link as people paste it, made the feed it stands for where the page doesn't name one: a Steam game's news, a
    // YouTube channel's videos.
    public static string Shortcut(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
            return link.Trim();
        if (uri.Host is "store.steampowered.com" or "steamcommunity.com" && SteamApp().Match(uri.AbsolutePath) is { Success: true } app)
            return $"https://store.steampowered.com/feeds/news/app/{app.Groups[1].Value}/";
        if (uri.Host.EndsWith("youtube.com") && YoutubeChannel().Match(uri.AbsolutePath) is { Success: true } channel)
            return $"https://www.youtube.com/feeds/videos.xml?channel_id={channel.Groups[1].Value}";
        return uri.AbsoluteUri;
    }

    // For adding one: the feed at the link, or the one the page there names.
    public async Task<(string Url, FeedPage? Page, string? Problem)> FindAsync(string link, CancellationToken ct)
    {
        var url = Shortcut(link);
        var (bytes, problem) = await GetAsync(url, null, null, ct);
        if (bytes is null)
            return (url, null, problem ?? "It answered with nothing.");
        if (FeedReader.Read(bytes.Value.Body) is { } page)
            return (url, await SteamNamedAsync(url, page, ct), null);
        if (FeedReader.Discover(Encoding.UTF8.GetString(bytes.Value.Body), new(url)) is not { } found)
            return (url, null, "That isn't a feed, and the page there doesn't name one.");
        (bytes, problem) = await GetAsync(found, null, null, ct);
        return bytes is not null && FeedReader.Read(bytes.Value.Body) is { } named
            ? (found, named, null)
            : (found, null, problem ?? "The feed the page names couldn't be read.");
    }

    // Steam's news feeds are titled with the game's number alone; its store has the name.
    private async Task<FeedPage> SteamNamedAsync(string url, FeedPage page, CancellationToken ct)
    {
        if (SteamNews().Match(url) is not { Success: true } app)
            return page;
        var (bytes, _) = await GetAsync($"https://store.steampowered.com/api/appdetails?appids={app.Groups[1].Value}&filters=basic", null, null, ct);
        try
        {
            using var details = bytes is { } got ? JsonDocument.Parse(got.Body) : null;
            return details?.RootElement.TryGetProperty(app.Groups[1].Value, out var found) == true
                && found.TryGetProperty("data", out var data) && data.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } game
                ? page with { Title = $"{game} news" }
                : page;
        }
        catch (JsonException)
        {
            return page;
        }
    }

    public async Task<FeedRead> ReadAsync(string url, string? etag, string? lastModified, CancellationToken ct)
    {
        var (got, problem) = await GetAsync(url, etag, lastModified, ct);
        if (problem is not null)
            return new(null, etag, lastModified, problem);
        if (got is not { } response)
            return new(null, etag, lastModified, null);
        return FeedReader.Read(response.Body) is { } page
            ? new(page, response.ETag, response.LastModified, null)
            : new(null, etag, lastModified, "It isn't a feed any more.");
    }

    // The body, or nothing when it hasn't changed (304), or why not.
    private async Task<((byte[] Body, string? ETag, string? LastModified)?, string?)> GetAsync(string url, string? etag, string? lastModified, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, "That isn't a web link.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, text/html;q=0.8, */*;q=0.5");
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, out var since))
            request.Headers.IfModifiedSince = since;
        // Past YouTube's cookie question, which pages in Europe get before the channel.
        if (uri.Host.EndsWith("youtube.com"))
            request.Headers.Add("Cookie", "SOCS=CAI");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return (null, null);
            if (!response.IsSuccessStatusCode)
                return (null, $"It answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return (null, "It's too big for a feed.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxBytes)
                    return (null, "It's too big for a feed.");
                body.Write(buffer, 0, read);
            }
            return ((body.ToArray(), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R")), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.InnerException is HttpRequestException inner ? inner.Message : ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "It didn't answer in time.");
        }
    }

    [GeneratedRegex(@"^/app/(\d+)")]
    private static partial Regex SteamApp();

    [GeneratedRegex(@"^https://store\.steampowered\.com/feeds/news/app/(\d+)/$")]
    private static partial Regex SteamNews();

    [GeneratedRegex(@"^/channel/(UC[\w-]{22})")]
    private static partial Regex YoutubeChannel();
}
