using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Discord;
using THOBOTTO.GameServers;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Reposts;

namespace THOBOTTO.Links;

public static class SocialChoices
{
    public const string Repost = "repost";
    public const string Reply = "reply";
    public const string Off = "off";
}

// Stored with SettingsStore under the module id.
public sealed record LinkRules
{
    [Setting("Links to other sites", Help = "Sites whose links show no preview in Discord (X, TikTok, Reddit, Bluesky, Instagram, Tumblr, Bilibili) have services that do; the member's link is swapped for one of those. Members can turn it off for theirs on their You page.",
        Choices = [SocialChoices.Repost + "=Post the message again under the member's name with links that show a preview (needs the Posting for members module)",
            SocialChoices.Reply + "=Reply with links that show a preview, the message's own previews hidden",
            SocialChoices.Off + "=Leave them"])]
    public string Social { get; init; } = SocialChoices.Repost;

    [Setting("Preview services", Kind = SettingKind.LongText, Help = "One a line: a site and the hosts that show its links with a preview, tried in turn, x.com=fixupx.com,vxtwitter.com. One that gives no picture or video for a link is passed over. These services come and go (the top of this page says how they've done): change a line when one stops working. A site with none (instagram.com=) is left to the bot fetching its videos.")]
    public string Services { get; init; } = string.Join('\n', LinkFixer.Defaults);

    [Setting("Short links followed first", Help = "Hosts, comma-separated: a link on one is followed to where it leads, then swapped as that site's links are. For short links the services can't read: v.redd.it gives the video's id, not the post's.")]
    public IReadOnlyList<string> ShortLinks { get; init; } = ["v.redd.it", "redd.it", "b23.tv"];

    [Setting("When no service previews it, the bot fetches the video", Help = "With yt-dlp, posted with the message (the link kept from previewing). Sites that want signing in (Instagram) need cookies on the Integrations page, from the bot's owner.")]
    public bool OwnVideos { get; init; } = true;

    [Setting("Largest video the bot fetches", Help = "Never more than the server may upload: 10 MB, 50 MB with boost level 2, 100 MB with level 3.", Unit = "MB", Min = 1, Max = 100)]
    public int OwnVideoMegabytes { get; init; } = 10;

    [Setting("Show links to messages", Help = "A link to a message in this server gets the message shown below it (who, what, its picture), when whoever posted the link can see it there.")]
    public bool MessageLinks { get; init; } = true;
}

// A link to a site that shows no preview, and the same link on each of the site's services, to try in turn.
public sealed record SiteLink(string Original, string Site, IReadOnlyList<string> Swaps);

public static partial class LinkFixer
{
    public static readonly IReadOnlyList<string> Defaults = ["x.com=fixupx.com,vxtwitter.com", "twitter.com=fixupx.com,vxtwitter.com", "tiktok.com=tnktok.com,tiktxk.com", "reddit.com=vxreddit.com,fxreddit.seria.moe", "bsky.app=fxbsky.app,bskyx.app",
        "instagram.com=eeinstagram.com,vxinstagram.com,oginstagram.com", "tumblr.com=tpmblr.com", "bilibili.com=vxbilibili.com,fxbilibili.seria.moe"];

    // site → the hosts that preview it, in the order to try them, from the setting's lines; none, for a site whose videos
    // only the bot fetches.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Services(string lines)
        => lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => (Site: p[0].ToLowerInvariant(), Hosts: (IReadOnlyList<string>)[.. p[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => h.ToLowerInvariant())]))
            .GroupBy(p => p.Site)
            .ToDictionary(g => g.Key, g => g.First().Hosts);

    // Each link to such a site, outside code and not one the member kept from previewing with <…>, on each of its
    // services: www., m. and mobile. dropped, other subdomains (vm.tiktok.com) kept.
    public static IReadOnlyList<SiteLink> Links(string text, IReadOnlyDictionary<string, IReadOnlyList<string>> services)
    {
        var found = new List<SiteLink>();
        foreach (var (link, host, path) in Found(text))
        {
            if (services.FirstOrDefault(s => host == s.Key || host.EndsWith("." + s.Key, StringComparison.Ordinal)) is { Key: { } site, Value: { } hosts })
                found.Add(new(link, site, [.. hosts.Select(h => $"https://{host[..^site.Length]}{h}{path}")]));
        }
        return found;
    }

    // The links on these hosts, the same way: short ones, to follow to where they lead.
    public static IReadOnlyList<string> Short(string text, IReadOnlyList<string> hosts)
        => [.. Found(text).Where(l => hosts.Contains(l.Host, StringComparer.OrdinalIgnoreCase)).Select(l => l.Link)];

    private static List<(string Link, string Host, string Path)> Found(string text)
    {
        var found = new List<(string, string, string)>();
        RepostText.OutsideCode(text, part =>
        {
            foreach (Match m in Link().Matches(part))
                found.Add((m.Value, Plain().Replace(m.Groups[1].Value.ToLowerInvariant(), ""), m.Groups[2].Value));
            return part;
        });
        return found;
    }

    // The text with the links given a replacement replaced, outside code.
    public static string Replace(string text, IReadOnlyDictionary<string, string> replacements)
        => RepostText.OutsideCode(text, part => Link().Replace(part, m => replacements.GetValueOrDefault(m.Value) ?? m.Value));

    [GeneratedRegex(@"(?<![<\w])https?://([A-Za-z0-9.-]+)(/[^\s<>()]+)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(www|m|mobile)\.")]
    private static partial Regex Plain();

    // discord.com/channels/{server}/{channel}/{message}, and its ptb., canary. and discordapp.com forms.
    [GeneratedRegex(@"https?://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/channels/(\d+)/(\d+)/(\d+)")]
    public static partial Regex MessageLink();
}

// Whether a link shows something in Discord: its page, as Discord's crawler is served it, has a picture or a video for
// previews, or the post as ActivityPub (FxEmbed's, where the pictures are), or it's the picture or video itself. A service
// that's gone (blocked, shut down) still answers, with none of those. Known for a while.
public sealed partial class PreviewCheck([FromKeyedServices(nameof(PreviewCheck))] HttpClient http, TimeProvider time, ILogger<PreviewCheck> logger)
{
    private const int Read = 256 * 1024;
    private const string Crawler = "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)";
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (bool Shows, DateTimeOffset At)> _known = new();
    // (site, service) → when it last gave a preview, and when it last gave none.
    private readonly ConcurrentDictionary<(string Site, string Host), (DateTimeOffset? Gave, DateTimeOffset? Failed)> _health = new();

    // The first of a link's services that previews it, tried in turn.
    public async Task<string?> FirstShowingAsync(string site, IReadOnlyList<string> swaps)
    {
        foreach (var swap in swaps)
        {
            var shows = await ShowsAsync(swap);
            var key = (site, new Uri(swap).Host);
            _health.AddOrUpdate(key, _ => shows ? (time.GetUtcNow(), null) : (null, time.GetUtcNow()),
                (_, was) => shows ? (time.GetUtcNow(), was.Failed) : (was.Gave, time.GetUtcNow()));
            if (shows)
                return swap;
        }
        return null;
    }

    // How each site's services did since the bot started, for the settings page.
    public IReadOnlyDictionary<string, IReadOnlyList<(string Host, DateTimeOffset? Gave, DateTimeOffset? Failed)>> Health()
        => _health.GroupBy(h => h.Key.Site).ToDictionary(g => g.Key, g => (IReadOnlyList<(string, DateTimeOffset?, DateTimeOffset?)>)[.. g.Select(h => (h.Key.Host, h.Value.Gave, h.Value.Failed))]);

    // Where a link leads, followed as Discord's crawler would be: its last address, or null.
    public async Task<string?> FollowAsync(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !await PublicAddress.IsPublicAsync(uri.IdnHost, CancellationToken.None))
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            request.Headers.UserAgent.ParseAdd(Crawler);
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode ? response.RequestMessage?.RequestUri?.AbsoluteUri : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Following {Link} failed: {Message}", link, ex.Message);
            return null;
        }
    }

    private async Task<bool> ShowsAsync(string link)
    {
        if (_known.TryGetValue(link, out var known) && time.GetUtcNow() - known.At < Kept)
            return known.Shows;
        var shows = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.UserAgent.ParseAdd(Crawler);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is { } type && (type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal)))
                shows = true;
            else if (response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[Read];
                var length = 0;
                for (int n; length < Read && (n = await body.ReadAsync(buffer.AsMemory(length))) > 0;)
                    length += n;
                shows = Preview().IsMatch(Encoding.UTF8.GetString(buffer, 0, length));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("Looking at {Link} for a preview failed: {Message}", link, ex.Message);
        }
        _known[link] = (shows, time.GetUtcNow());
        return shows;
    }

    [GeneratedRegex("""<meta[^>]+(property|name)=["'](og:image|og:video|twitter:image|twitter:player)["']|<link[^>]+type=["']application/activity\+json["']""", RegexOptions.IgnoreCase)]
    private static partial Regex Preview();
}

// What a message's links to sites without previews become: each on the first of its services that previews it, or,
// when none does (as set), kept from previewing with the bot's own copy of its video beside it.
public sealed record LinkAnswer(IReadOnlyDictionary<string, string> Replacements, IReadOnlyList<(string Name, byte[] Bytes)> Files);

public sealed class LinkAnswers(PreviewCheck check, LinkVideos videos)
{
    private const int MostLinks = 3;

    public async Task<LinkAnswer> ForAsync(string text, LinkRules rules, Guild guild)
    {
        var replacements = new Dictionary<string, string>();
        var files = new List<(string, byte[])>();
        var limit = Math.Min(rules.OwnVideoMegabytes * 1024L * 1024, UploadLimit(guild));
        var services = LinkFixer.Services(rules.Services);
        var links = LinkFixer.Links(text, services).ToList();
        foreach (var link in LinkFixer.Short(text, rules.ShortLinks).Distinct().Take(MostLinks))
        {
            if (await check.FollowAsync(link) is { } to && LinkFixer.Links(to, services) is [var site])
                links.Add(site with { Original = link });
        }
        foreach (var link in links.DistinctBy(l => l.Original).Take(MostLinks))
        {
            if (await check.FirstShowingAsync(link.Site, link.Swaps) is { } swapped)
                replacements[link.Original] = swapped;
            else if (rules.OwnVideos && await videos.FetchAsync(link.Original, limit) is { } video)
            {
                files.Add(video);
                replacements[link.Original] = $"<{link.Original}>";
            }
        }
        return new(replacements, files);
    }

    // What the bot may upload in a message there: more with the server's boosts.
    public static long UploadLimit(Guild guild) => guild.PremiumTier switch { 2 => 50, 3 => 100, _ => 10 } * 1024L * 1024;
}

// Links to sites without previews swapped for services that preview them (or given the bot's own copy of the video),
// the message posted again under the member's name.
public sealed class SocialLinks(SettingsStore settings, ModuleState modules, LinkAnswers answers) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Words;

    public bool Wants(string text, ulong channelId, ulong userId) => text.Contains("http", StringComparison.OrdinalIgnoreCase);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (!preference.LinkFixes || !await modules.IsEnabledAsync(guildId, LinkReplies.ModuleId))
            return;
        var rules = await settings.GetAsync<LinkRules>(guildId, LinkReplies.ModuleId);
        if (rules.Social != SocialChoices.Repost)
            return;
        var answer = await answers.ForAsync(repost.Text, rules, repost.Guild);
        if (answer.Replacements.Count == 0)
            return;
        (repost.Text, repost.Changed) = (LinkFixer.Replace(repost.Text, answer.Replacements), true);
        repost.Files.AddRange(answer.Files);
    }
}

// What the bot says below a message: links that preview (set to reply), and the messages it links to.
public sealed class LinkReplies(SettingsStore settings, ModuleState modules, PostingPreferences preferences, LinkAnswers answers, AsMember asMember, GatewayClient gateway, RestClient rest, ILogger<LinkReplies> logger)
{
    public const string ModuleId = "links";
    private const int MostShown = 3;
    private const int Shown = 1000;

    public async Task OnMessageAsync(Message message)
    {
        if (message is not { GuildId: { } guildId, WebhookId: null, Author: GuildUser { IsBot: false } member }
            || !message.Content.Contains("http", StringComparison.OrdinalIgnoreCase)
            || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild))
            return;
        try
        {
            if (!await modules.IsEnabledAsync(guildId, ModuleId))
                return;
            var rules = await settings.GetAsync<LinkRules>(guildId, ModuleId);
            if (rules.Social == SocialChoices.Reply && (await preferences.GetAsync(member.Id)).LinkFixes
                && await answers.ForAsync(message.Content, rules, guild) is { Replacements.Count: > 0 } answer)
                await ReplyFixedAsync(message, member, answer);
            if (rules.MessageLinks)
                await ShowLinkedAsync(message, guild, member);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Answering the links in message {MessageId} failed", message.Id);
        }
    }

    // Reactions to the answer count for the member, as they would on theirs posted again.
    private async Task ReplyFixedAsync(Message message, GuildUser member, LinkAnswer answer)
    {
        var files = answer.Files.Select(f => new MemoryStream(f.Bytes)).ToList();
        try
        {
            var reply = await rest.SendMessageAsync(message.ChannelId, new()
            {
                Content = string.Join('\n', answer.Replacements.Values.Where(r => !r.StartsWith('<')).Take(MostShown)),
                Attachments = [.. answer.Files.Zip(files, (f, stream) => new AttachmentProperties(f.Name, stream))],
                MessageReference = MessageReferenceProperties.Reply(message.Id, failIfNotExists: false),
                AllowedMentions = AllowedMentionsProperties.None,
                Flags = MessageFlags.SuppressNotifications,
            });
            await asMember.CreditAsync(reply, member.GuildId, member.Id);
        }
        finally
        {
            foreach (var file in files)
                await file.DisposeAsync();
        }
        try
        {
            await rest.ModifyMessageAsync(message.ChannelId, message.Id, m => m.Flags = MessageFlags.SuppressEmbeds);
        }
        catch (RestException ex)
        {
            logger.LogInformation("Hiding the previews of {MessageId} failed: {Problem}", message.Id, ex.Message);
        }
    }

    // Each message linked to in this server that the member can see there, shown below theirs (not as a reply: their
    // message may be posted again).
    private async Task ShowLinkedAsync(Message message, Guild guild, GuildUser member)
    {
        var here = guild.Channels.GetValueOrDefault(message.ChannelId) as TextGuildChannel ?? guild.ActiveThreads.GetValueOrDefault(message.ChannelId);
        var embeds = new List<EmbedProperties>();
        foreach (var link in LinkFixer.MessageLink().Matches(message.Content).Take(MostShown))
        {
            if (ulong.Parse(link.Groups[1].Value) != guild.Id
                || (guild.Channels.GetValueOrDefault(ulong.Parse(link.Groups[2].Value)) as TextGuildChannel ?? guild.ActiveThreads.GetValueOrDefault(ulong.Parse(link.Groups[2].Value))) is not { } there
                || (there.Nsfw && here?.Nsfw != true))
                continue;
            var seen = member.GetChannelPermissions(guild, there is GuildThread thread ? thread.ParentId : there.Id);
            if (!seen.HasFlag(Permissions.Administrator) && !seen.HasFlag(Permissions.ViewChannel | Permissions.ReadMessageHistory))
                continue;
            RestMessage linked;
            try
            {
                linked = await rest.GetMessageAsync(there.Id, ulong.Parse(link.Groups[3].Value));
            }
            catch (RestException)
            {
                continue;
            }
            embeds.Add(await EmbedAsync(guild, there, linked));
        }
        if (embeds.Count > 0)
            await rest.SendMessageAsync(message.ChannelId, new() { Embeds = embeds, AllowedMentions = AllowedMentionsProperties.None, Flags = MessageFlags.SuppressNotifications });
    }

    private async Task<EmbedProperties> EmbedAsync(Guild guild, TextGuildChannel there, RestMessage linked)
    {
        var author = guild.Users.GetValueOrDefault(await asMember.AuthorOfAsync(linked)) is { } shown
            ? (Name: shown.Nickname ?? shown.GlobalName ?? shown.Username, Avatar: AsMember.AvatarOf(shown))
            : (Name: linked.Author.GlobalName ?? linked.Author.Username, Avatar: (linked.Author.GetAvatarUrl() ?? linked.Author.DefaultAvatarUrl).ToString());
        var text = linked.Content.Length > Shown ? linked.Content[..Shown] + "…" : linked.Content;
        var picture = linked.Attachments.FirstOrDefault(a => a.ContentType?.StartsWith("image/") == true)?.Url ?? linked.Embeds.FirstOrDefault(e => e.Image is not null)?.Image?.Url;
        var link = Notifier.Link(guild.Id, there.Id, linked.Id);
        return new()
        {
            Author = new() { Name = author.Name, IconUrl = author.Avatar, Url = link },
            Description = $"{(string.IsNullOrWhiteSpace(text) ? "" : text + "\n\n")}-# [#{there.Name}]({link}) · <t:{linked.CreatedAt.ToUnixTimeSeconds()}:R>",
            Image = picture,
        };
    }
}

public sealed class LinkHandler(LinkReplies replies) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg) => await replies.OnMessageAsync(arg);
}

// How the preview services have done since the bot started, at the top of the settings page.
public sealed class LinksSummary(SettingsStore settings, PreviewCheck check, LinkVideos videos, TimeProvider time) : ISettingsSummary
{
    public string ModuleId => LinkReplies.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<LinkRules>(guildId, ModuleId);
        var health = check.Health();
        var sites = LinkFixer.Services(rules.Services).Keys.Where(health.ContainsKey).Select(site =>
        {
            var hosts = health[site];
            return hosts.Where(h => h.Gave is not null && (h.Failed is null || h.Gave > h.Failed)).OrderByDescending(h => h.Gave).FirstOrDefault() is { Host: { } working } best
                ? $"{site} through {working} ({Ago(best.Gave!.Value)})"
                : $"{site}: none gave a preview lately ({string.Join(", ", hosts.Select(h => h.Host))})";
        }).ToList();
        var own = videos.Health switch
        {
            ({ } fetched, var failed) when failed is null || fetched > failed => $"; the bot fetched a video itself {Ago(fetched)}",
            (_, { } failed) => $"; the bot couldn't fetch a video itself {Ago(failed)}",
            _ => "",
        };
        return sites.Count == 0 ? "Now: no links looked at since the bot started." : $"Now: {string.Join("; ", sites)}{own}.";
    }

    private string Ago(DateTimeOffset at) => (time.GetUtcNow() - at) switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } since => $"{(int)since.TotalMinutes} min ago",
        { TotalDays: < 1 } since => $"{(int)since.TotalHours} h ago",
        var since => $"{(int)since.TotalDays} days ago",
    };
}

public static class LinksStartup
{
    public static IServiceCollection AddLinks(this IServiceCollection services)
    {
        services.AddHttpClient(nameof(PreviewCheck), c => c.Timeout = TimeSpan.FromSeconds(5)).AddAsKeyed(ServiceLifetime.Singleton);
        return services
            .AddSingleton<PreviewCheck>()
            .AddSingleton<LinkVideos>()
            .AddSingleton<LinkAnswers>()
            .AddSingleton<ISettingsSummary, LinksSummary>()
            .AddSingleton<LinkReplies>()
            .AddSingleton<IRewrite, SocialLinks>();
    }
}
