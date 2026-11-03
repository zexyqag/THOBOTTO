using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Discord;
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
    [Setting("Links to other sites", Help = "Sites whose links show no preview in Discord (X, TikTok, Reddit, Bluesky) have services that do; the member's link is swapped for one of those. Members can turn it off for theirs on their You page.",
        Choices = [SocialChoices.Repost + "=Post the message again under the member's name with links that show a preview (needs the Posting for members module)",
            SocialChoices.Reply + "=Reply with links that show a preview, the message's own previews hidden",
            SocialChoices.Off + "=Leave them"])]
    public string Social { get; init; } = SocialChoices.Repost;

    [Setting("Preview services", Kind = SettingKind.LongText, Help = "One a line: a site and the host that shows its links with a preview, x.com=fixupx.com. These services come and go: change or remove a line when one stops working, or add one (instagram.com=… when there's a good one).")]
    public string Services { get; init; } = string.Join('\n', LinkFixer.Defaults);

    [Setting("Show links to messages", Help = "A link to a message in this server gets the message shown below it (who, what, its picture), when whoever posted the link can see it there.")]
    public bool MessageLinks { get; init; } = true;
}

public static partial class LinkFixer
{
    public static readonly IReadOnlyList<string> Defaults = ["x.com=fixupx.com", "twitter.com=fixupx.com", "tiktok.com=vxtiktok.com", "reddit.com=rxddit.com", "bsky.app=fxbsky.app"];

    // site → the host that previews it, from the setting's lines.
    public static IReadOnlyDictionary<string, string> Services(string lines)
        => lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
            .GroupBy(p => p[0].ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].ToLowerInvariant());

    // The text with each link to such a site (outside code, not one the member kept from previewing with <…>) on its
    // service instead; www., m. and mobile. dropped, other subdomains (vm.tiktok.com) kept.
    public static (string Text, int Fixed) Fix(string text, IReadOnlyDictionary<string, string> services)
    {
        var count = 0;
        var fixedText = RepostText.OutsideCode(text, part => Link().Replace(part, m =>
        {
            var host = Plain().Replace(m.Groups[1].Value.ToLowerInvariant(), "");
            foreach (var (site, service) in services)
            {
                if (host == site || host.EndsWith("." + site, StringComparison.Ordinal))
                {
                    count++;
                    return $"https://{host[..^site.Length]}{service}{m.Groups[2].Value}";
                }
            }
            return m.Value;
        }));
        return (fixedText, count);
    }

    // The links Fix would make, for a reply.
    public static IReadOnlyList<string> Fixed(string text, IReadOnlyDictionary<string, string> services)
    {
        var found = new List<string>();
        RepostText.OutsideCode(text, part =>
        {
            foreach (Match m in Link().Matches(part))
            {
                if (Fix(m.Value, services) is (var link, > 0))
                    found.Add(link);
            }
            return part;
        });
        return found;
    }

    [GeneratedRegex(@"(?<![<\w])https?://([A-Za-z0-9.-]+)(/[^\s<>()]+)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(www|m|mobile)\.")]
    private static partial Regex Plain();

    // discord.com/channels/{server}/{channel}/{message}, and its ptb., canary. and discordapp.com forms.
    [GeneratedRegex(@"https?://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/channels/(\d+)/(\d+)/(\d+)")]
    public static partial Regex MessageLink();
}

// Links to sites without previews swapped for services that preview them, the message posted again under the member's
// name.
public sealed class SocialLinks(SettingsStore settings, ModuleState modules) : IRewrite
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
        var (text, count) = LinkFixer.Fix(repost.Text, LinkFixer.Services(rules.Services));
        if (count > 0)
            (repost.Text, repost.Changed) = (text, true);
    }
}

// What the bot says below a message: links that preview (set to reply), and the messages it links to.
public sealed class LinkReplies(SettingsStore settings, ModuleState modules, PostingPreferences preferences, AsMember asMember, GatewayClient gateway, RestClient rest, ILogger<LinkReplies> logger)
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
                && LinkFixer.Fixed(message.Content, LinkFixer.Services(rules.Services)) is { Count: > 0 } links)
                await ReplyFixedAsync(message, links);
            if (rules.MessageLinks)
                await ShowLinkedAsync(message, guild, member);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Answering the links in message {MessageId} failed", message.Id);
        }
    }

    private async Task ReplyFixedAsync(Message message, IReadOnlyList<string> links)
    {
        await rest.SendMessageAsync(message.ChannelId, new()
        {
            Content = string.Join('\n', links.Take(MostShown)),
            MessageReference = MessageReferenceProperties.Reply(message.Id, failIfNotExists: false),
            AllowedMentions = AllowedMentionsProperties.None,
            Flags = MessageFlags.SuppressNotifications,
        });
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

public static class LinksStartup
{
    public static IServiceCollection AddLinks(this IServiceCollection services)
        => services
            .AddSingleton<LinkReplies>()
            .AddSingleton<IRewrite, SocialLinks>();
}
