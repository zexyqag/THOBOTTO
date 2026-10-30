using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Modules;

namespace THOBOTTO.Feeds;

// Reads each server's feeds as often as it says and posts what's new where each feed goes.
public sealed class FeedWatch(
    FeedFetcher fetcher,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    RestClient rest,
    GatewayClient gateway,
    TimeProvider time,
    ILogger<FeedWatch> logger) : BackgroundService
{
    public const string ModuleId = "feeds";
    public const int MaxFeeds = 50;

    private const int SeenKept = 300;
    // A check posts at most this many, the newest; the rest are only marked seen (a feed that changed all its ids).
    private const int PostedAtOnce = 5;
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    // An entry this old when first seen isn't news: a feed that brought back an old one.
    private static readonly TimeSpan Stale = TimeSpan.FromDays(3);

    public async Task<IReadOnlyList<NewsFeed>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.NewsFeeds.AsNoTracking().Where(f => f.GuildId == guildId).OrderBy(f => f.Title).ToListAsync();
    }

    // Follows the feed at the link (or the one its page names) and posts its newest entry, so the channel shows how
    // they'll look; the feed, or why not.
    public async Task<(NewsFeed? Feed, string? Problem)> AddAsync(ulong guildId, string link, ulong channelId, ulong actorId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.NewsFeeds.CountAsync(f => f.GuildId == guildId, ct) >= MaxFeeds)
            return (null, $"This server already follows {MaxFeeds} feeds, the most it takes.");
        var (url, page, problem) = await fetcher.FindAsync(link, ct);
        if (page is null)
            return (null, $"That feed couldn't be read: {problem}");
        if (await db.NewsFeeds.AnyAsync(f => f.GuildId == guildId && f.Url == url && f.ChannelId == channelId, ct))
            return (null, "That feed already goes there.");
        var feed = new NewsFeed
        {
            GuildId = guildId,
            Url = url,
            Title = Clip(page.Title, 200),
            ChannelId = channelId,
            AddedBy = actorId,
            CreatedAt = time.GetUtcNow(),
            Seen = JsonSerializer.Serialize(page.Items.Select(i => i.Key).Take(SeenKept)),
            CheckedAt = time.GetUtcNow(),
        };
        db.NewsFeeds.Add(feed);
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "feeds.add", Details = url, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        if (page.Items.OrderByDescending(i => i.Published).FirstOrDefault() is { } newest)
        {
            feed.Problem = await PostAsync(feed, page, newest);
            if (feed.Problem is null)
                feed.PostedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return (feed, null);
    }

    public async Task<NewsFeed?> RemoveAsync(ulong guildId, long feedId, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.NewsFeeds.FirstOrDefaultAsync(f => f.GuildId == guildId && f.Id == feedId) is not { } feed)
            return null;
        db.NewsFeeds.Remove(feed);
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "feeds.remove", Details = feed.Url, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return feed;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await CheckDueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checking the news feeds failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task CheckDueAsync(CancellationToken ct)
    {
        List<NewsFeed> feeds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            feeds = await db.NewsFeeds.AsNoTracking().ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var guildFeeds in feeds.GroupBy(f => f.GuildId))
        {
            // Servers the bot isn't in (or hasn't heard of yet since it started) wait.
            if (!gateway.Cache.Guilds.ContainsKey(guildFeeds.Key) || !await modules.IsEnabledAsync(guildFeeds.Key, ModuleId))
                continue;
            var every = TimeSpan.FromMinutes((await settings.GetAsync<FeedRules>(guildFeeds.Key, ModuleId)).CheckMinutes);
            // A feed that keeps failing is read less often, up to eight times as seldom.
            foreach (var feed in guildFeeds.Where(f => f.CheckedAt is not { } at || now - at >= every * Math.Min(1 + f.Failures, 8)))
                await CheckAsync(feed.Id, ct);
        }
    }

    private async Task CheckAsync(long feedId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.NewsFeeds.FindAsync([feedId], ct) is not { } feed)
            return;
        using var _ = logger.BeginScope(DebugLog.Guild(feed.GuildId));
        var read = await fetcher.ReadAsync(feed.Url, feed.ETag, feed.LastModified, ct);
        var now = time.GetUtcNow();
        feed.CheckedAt = now;
        if (read.Problem is { } problem)
        {
            (feed.Problem, feed.Failures) = (problem, feed.Failures + 1);
            if (feed.Failures == 3)
                logger.LogWarning("The feed {Title} ({Url}) couldn't be read three times in a row: {Problem}", feed.Title, feed.Url, problem);
            await db.SaveChangesAsync(ct);
            return;
        }
        (feed.Problem, feed.Failures, feed.ETag, feed.LastModified) = (null, 0, read.ETag, read.LastModified);
        if (read.Page is { } page)
        {
            var seen = JsonSerializer.Deserialize<List<string>>(feed.Seen) ?? [];
            var known = seen.ToHashSet();
            // Oldest first, as they came: feeds list the newest first, and not all of them give dates.
            var fresh = page.Items.Where(i => !known.Contains(i.Key)).Reverse()
                .OrderBy(i => i.Published ?? now)
                .Where(i => i.Published is not { } at || now - at < Stale)
                .TakeLast(PostedAtOnce);
            foreach (var item in fresh)
            {
                if (await PostAsync(feed, page, item) is { } failed)
                {
                    feed.Problem = failed;
                    break;
                }
                feed.PostedAt = now;
            }
            feed.Seen = JsonSerializer.Serialize(page.Items.Select(i => i.Key).Concat(seen).Distinct().Take(SeenKept));
        }
        await db.SaveChangesAsync(ct);
    }

    // Null when posted, else why not.
    private async Task<string?> PostAsync(NewsFeed feed, FeedPage page, FeedItem item)
    {
        if (!gateway.Cache.Guilds.TryGetValue(feed.GuildId, out var guild) || !guild.Channels.TryGetValue(feed.ChannelId, out var channel))
            return "Its channel is gone.";
        var (content, embeds) = Message(feed, page, item);
        try
        {
            if (channel is ForumGuildChannel)
                await rest.CreateForumGuildThreadAsync(channel.Id, new(Clip(item.Title, 100), new() { Content = content, Embeds = embeds, AllowedMentions = AllowedMentionsProperties.None }));
            else
                await rest.SendMessageAsync(channel.Id, new() { Content = content, Embeds = embeds, AllowedMentions = AllowedMentionsProperties.None });
            return null;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            logger.LogWarning("Posting from the feed {Title} in {ChannelId} failed: {Message}", feed.Title, channel.Id, ex.Message);
            return $"It couldn't be posted in #{channel.Name}: {ex.Message}";
        }
    }

    // A video as its link alone, so Discord shows its player; anything else as a card.
    private static (string? Content, EmbedProperties[] Embeds) Message(NewsFeed feed, FeedPage page, FeedItem item)
    {
        if (item.Link is { } video && Uri.TryCreate(video, UriKind.Absolute, out var uri)
            && (uri.Host.EndsWith("youtube.com") && (uri.AbsolutePath == "/watch" || uri.AbsolutePath.StartsWith("/shorts/") || uri.AbsolutePath.StartsWith("/live/")) || uri.Host == "youtu.be"))
            return ($"📺 **{feed.Title}**: {item.Title}\n{video}", []);
        return (null,
        [
            new EmbedProperties
            {
                Author = new EmbedAuthorProperties { Name = Clip(feed.Title, 256), Url = Web(page.Link) },
                Title = Clip(item.Title, 256),
                Url = Web(item.Link),
                Description = item.Summary,
                Image = Web(item.Image) is { } image ? new EmbedImageProperties(image) : null,
                Timestamp = item.Published,
                Color = new(0x5865F2),
            },
        ]);
    }

    private static string? Web(string? link) => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
