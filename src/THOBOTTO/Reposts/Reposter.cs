using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Archive;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Reposts;

public enum RewriteStage
{
    // Who it's posted as (a character, nobody named): new messages only.
    Who,
    // What it says (kao: words, :emoji:, links): on edits too.
    Words,
}

// A member's message on its way to being posted again under their name, as each rewrite leaves it.
public sealed class Repost
{
    public required Guild Guild { get; init; }

    public required GuildUser Member { get; init; }

    public required TextGuildChannel Channel { get; init; }

    public required string Text { get; set; }

    // The message it comes from; none when a post is edited.
    public Message? Message { get; init; }

    // Another name and picture than the member's (a character's); with Anonymous, no username beside it.
    public string? Name { get; set; }

    public string? AvatarUrl { get; set; }

    public bool Anonymous { get; set; }

    public bool Changed { get; set; }

    // Done once it's posted, not on an edit (favourite faces counted).
    public List<Func<Task>> Then { get; } = [];
}

// One feature's change to members' messages; any that changes a message has it posted again.
public interface IRewrite
{
    RewriteStage Stage { get; }

    // A cheap first look, before anything is loaded.
    bool Wants(string text, ulong channelId, ulong userId);

    Task RewriteAsync(Repost repost, PostingPreference preference);
}

// Members' messages a rewrite changed, posted again under their name (a reply with a line linking what it answered,
// files carried over) and theirs removed. Only where the bot may make webhooks and remove messages; a message it can't
// carry over whole (a sticker, a poll, a voice message, too long once rewritten) stays as written.
public sealed class Reposter(
    IEnumerable<IRewrite> rewrites,
    AsMember asMember,
    PostingPreferences preferences,
    SettingsStore settings,
    ModuleState modules,
    DeletionWitness witness,
    GatewayClient gateway,
    RestClient rest,
    [FromKeyedServices(nameof(Reposter))] HttpClient http,
    ILogger<Reposter> logger)
{
    public const string ModuleId = "reposts";
    private const long MostFiles = 25 * 1024 * 1024;

    private readonly IReadOnlyList<IRewrite> _rewrites = [.. rewrites.OrderBy(r => r.Stage)];

    // The bot may post as members there and remove theirs (a thread's parent decides).
    public bool Allowed(Guild guild, TextGuildChannel channel)
    {
        if (!guild.Users.TryGetValue(gateway.Id, out var bot))
            return false;
        var allowed = bot.GetChannelPermissions(guild, channel is GuildThread thread ? thread.ParentId : channel.Id);
        return allowed.HasFlag(Permissions.Administrator) || allowed.HasFlag(Permissions.ManageWebhooks | Permissions.ManageMessages);
    }

    public async Task OnMessageAsync(Message message)
    {
        if (message is not { GuildId: { } guildId, WebhookId: null, Author: GuildUser { IsBot: false } member, IsTts: false, Poll: null }
            || message.Type is not (MessageType.Default or MessageType.Reply)
            || message.Stickers.Count > 0 || message.MessageSnapshots.Count > 0 || message.Flags.HasFlag(MessageFlags.IsVoiceMessage)
            || message.Attachments.Sum(a => a.Size) > MostFiles)
            return;
        var wanting = _rewrites.Where(r => r.Wants(message.Content, message.ChannelId, member.Id)).ToList();
        if (wanting.Count == 0
            || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild)
            || (guild.Channels.GetValueOrDefault(message.ChannelId) ?? guild.ActiveThreads.GetValueOrDefault(message.ChannelId)) is not TextGuildChannel channel
            // A forum post's first message: removing it would remove the post.
            || channel.Id == message.Id
            || !Allowed(guild, channel))
            return;
        try
        {
            if (!await modules.IsEnabledAsync(guildId, ModuleId))
                return;
            var rules = await settings.GetAsync<RepostRules>(guildId, ModuleId);
            if (rules.ExcludedChannelIds.Contains(channel is GuildThread thread ? thread.ParentId : channel.Id))
                return;
            var repost = new Repost { Guild = guild, Member = member, Channel = channel, Text = message.Content, Message = message };
            var preference = await preferences.GetAsync(member.Id);
            foreach (var rewrite in wanting)
                await rewrite.RewriteAsync(repost, preference);
            if (!repost.Changed)
                return;
            if (message.MessageReference is { Type: MessageReferenceType.Reply } reply)
            {
                var to = message.ReferencedMessage is { } replied ? $"<@{await asMember.AuthorOfAsync(replied)}> · " : "";
                repost.Text = $"-# ↪ {to}{Notifier.Link(guildId, reply.ChannelId, reply.MessageId)}\n{repost.Text}";
            }
            if (repost.Text.Length > 2000 || !await RepostAsync(message, repost, rules))
                return;
            foreach (var then in repost.Then)
                await then();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Posting message {MessageId} again failed", message.Id);
        }
    }

    // A post's new text, its words rewritten again (kao: words, :emoji:, links).
    public async Task<string> RewriteAsync(Guild guild, GuildUser member, TextGuildChannel channel, string text)
    {
        var repost = new Repost { Guild = guild, Member = member, Channel = channel, Text = text };
        var preference = await preferences.GetAsync(member.Id);
        foreach (var rewrite in _rewrites.Where(r => r.Stage == RewriteStage.Words && r.Wants(text, channel.Id, member.Id)))
            await rewrite.RewriteAsync(repost, preference);
        return repost.Text;
    }

    private async Task<bool> RepostAsync(Message message, Repost repost, RepostRules rules)
    {
        var files = new List<MemoryStream>();
        try
        {
            var attachments = new List<AttachmentProperties>();
            foreach (var file in message.Attachments)
            {
                files.Add(new MemoryStream(await http.GetByteArrayAsync(file.Url)));
                attachments.Add(new AttachmentProperties(file.FileName, files[^1]) { Description = file.Description });
            }
            var flags = message.Flags & (MessageFlags.SuppressEmbeds | MessageFlags.SuppressNotifications);
            var name = repost.Name is not { } named ? null
                : repost.Anonymous || !rules.ShowUsername ? named
                : $"{named} ({repost.Member.Username})";
            var (posted, _) = await asMember.PostAsync(repost.Channel, repost.Member, rules.ShowUsername, new()
            {
                Content = repost.Text,
                Username = name,
                AvatarUrl = repost.AvatarUrl,
                Attachments = attachments,
                Flags = flags == 0 ? null : flags,
            });
            if (posted is null)
                return false;
            witness.Expect([message.Id], repost.Member.Id);
            try
            {
                await rest.DeleteMessageAsync(message.ChannelId, message.Id);
                return true;
            }
            catch (RestException ex)
            {
                // Theirs stays (or they removed it meanwhile), so the copy goes.
                logger.LogInformation("Removing message {MessageId} for its copy failed: {Problem}", message.Id, ex.Message);
                if (await asMember.FindAsync(posted.Id) is { } post)
                    await asMember.DeleteAsync(post);
                return false;
            }
        }
        finally
        {
            foreach (var file in files)
                await file.DisposeAsync();
        }
    }
}

public sealed class RepostHandler(Reposter reposter) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg) => await reposter.OnMessageAsync(arg);
}
