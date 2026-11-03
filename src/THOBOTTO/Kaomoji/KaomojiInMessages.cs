using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Archive;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Kaomoji;

// kao:shrug in a message: posted again under the member's name with the faces in it (a reply with a line linking what
// it answered, files carried over), and theirs removed. Only where the bot may make webhooks and remove messages; a
// message it can't carry over whole (a sticker, a poll, a voice message, too long with its faces) stays as written.
public sealed class KaomojiWriter(
    KaomojiShelf shelf,
    KaomojiFavourites favourites,
    KaomojiPreferences preferences,
    AsMember asMember,
    SettingsStore settings,
    ModuleState modules,
    DeletionWitness witness,
    GatewayClient gateway,
    RestClient rest,
    [FromKeyedServices(nameof(KaomojiWriter))] HttpClient http,
    ILogger<KaomojiWriter> logger)
{
    private const long MostFiles = 25 * 1024 * 1024;

    public async Task OnMessageAsync(Message message)
    {
        if (!KaomojiLibrary.HasWords(message.Content)
            || message is not { GuildId: { } guildId, WebhookId: null, Author: GuildUser { IsBot: false } member, IsTts: false, Poll: null }
            || message.Type is not (MessageType.Default or MessageType.Reply)
            || message.Stickers.Count > 0 || message.MessageSnapshots.Count > 0 || message.Flags.HasFlag(MessageFlags.IsVoiceMessage)
            || message.Attachments.Sum(a => a.Size) > MostFiles
            || !gateway.Cache.Guilds.TryGetValue(guildId, out var guild)
            || (guild.Channels.GetValueOrDefault(message.ChannelId) ?? guild.ActiveThreads.GetValueOrDefault(message.ChannelId)) is not TextGuildChannel channel
            // A forum post's first message: removing it would remove the post.
            || channel.Id == message.Id
            || !guild.Users.TryGetValue(gateway.Id, out var bot))
            return;
        var allowed = bot.GetChannelPermissions(guild, channel is GuildThread thread ? thread.ParentId : channel.Id);
        if (!allowed.HasFlag(Permissions.Administrator) && !allowed.HasFlag(Permissions.ManageWebhooks | Permissions.ManageMessages))
            return;
        try
        {
            if (!await modules.IsEnabledAsync(guildId, KaomojiShelf.ModuleId))
                return;
            var rules = await settings.GetAsync<KaomojiRules>(guildId, KaomojiShelf.ModuleId);
            if (!rules.PostAsMember || !rules.InMessages || !(await preferences.GetAsync(member.Id)).InMessages)
                return;
            var (text, used) = KaomojiLibrary.Fill(message.Content, await shelf.InAsync(guildId), await favourites.TextsAsync(member.Id));
            if (used.Count == 0)
                return;
            if (message.MessageReference is { Type: MessageReferenceType.Reply } reply)
            {
                var to = message.ReferencedMessage is { } replied ? $"<@{await asMember.AuthorOfAsync(replied)}> · " : "";
                text = $"-# ↪ {to}{Notifier.Link(guildId, reply.ChannelId, reply.MessageId)}\n{text}";
            }
            if (text.Length > 2000)
                return;
            await RepostAsync(message, channel, member, rules.ShowUsername, text);
            foreach (var face in used.DistinctBy(f => f.Text))
                await favourites.UsedAsync(member.Id, face.Text);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Filling in kao: words in message {MessageId} failed", message.Id);
        }
    }

    private async Task RepostAsync(Message message, TextGuildChannel channel, GuildUser member, bool withUsername, string text)
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
            var (posted, _) = await asMember.PostAsync(channel, member, withUsername, new() { Content = text, Attachments = attachments, Flags = flags == 0 ? null : flags });
            if (posted is null)
                return;
            witness.Expect([message.Id], member.Id);
            try
            {
                await rest.DeleteMessageAsync(message.ChannelId, message.Id);
            }
            catch (RestException ex)
            {
                // Theirs stays (or they removed it meanwhile), so the copy goes.
                logger.LogInformation("Removing message {MessageId} for its kao: copy failed: {Problem}", message.Id, ex.Message);
                if (await asMember.FindAsync(posted.Id) is { } post)
                    await asMember.DeleteAsync(post);
            }
        }
        finally
        {
            foreach (var file in files)
                await file.DisposeAsync();
        }
    }
}

public sealed class KaomojiMessageHandler(KaomojiWriter writer) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message arg) => await writer.OnMessageAsync(arg);
}

// Right-click a post the bot made under your name, Apps, Edit my post: its text changed (kao: words filled in again),
// or emptied to remove it.
public sealed class EditMyPostCommand(AsMember asMember) : GuildCommandModule
{
    [MessageCommand("Edit my post", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> EditAsync(RestMessage message)
    {
        if (message.WebhookId is null || await asMember.FindAsync(message.Id) is not { } post || post.UserId != Context.User.Id)
            return InteractionCallback.Message(Replies.Ephemeral("Only a post the bot made under your name can be edited here: a face from /kaomoji, or a message with kao: words."));
        return InteractionCallback.Modal(new ModalProperties($"editmypost:{message.Id}", "Edit your post")
        {
            new LabelProperties("Its text (empty removes it)", new TextInputProperties("text", TextInputStyle.Paragraph) { Value = message.Content, MaxLength = 2000, Required = false }),
        });
    }
}

public sealed class EditMyPostModal(AsMember asMember, KaomojiShelf shelf, KaomojiFavourites favourites, ModuleState modules) : GuildModalModule
{
    [ComponentInteraction("editmypost")]
    public async Task<InteractionCallbackProperties> EditAsync(ulong messageId)
    {
        if (await asMember.FindAsync(messageId) is not { } post || post.UserId != Context.User.Id)
            return InteractionCallback.Message(Replies.Ephemeral("That post is gone."));
        var text = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().First().Value.Trim();
        if (text.Length == 0)
            return InteractionCallback.Message(Replies.Ephemeral(await asMember.DeleteAsync(post) is { } unremoved ? $"It couldn't be removed ({unremoved})." : "Removed."));
        if (await modules.IsEnabledAsync(GuildId, KaomojiShelf.ModuleId))
            (text, _) = KaomojiLibrary.Fill(text, await shelf.InAsync(GuildId), await favourites.TextsAsync(Context.User.Id));
        if (text.Length > 2000)
            return InteractionCallback.Message(Replies.Ephemeral("With its faces it's longer than Discord's 2,000 characters."));
        return InteractionCallback.Message(Replies.Ephemeral(await asMember.EditAsync(post, text) is { } problem ? $"It couldn't be edited ({problem})." : "Edited."));
    }
}
