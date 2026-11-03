using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;
using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;
using THOBOTTO.Reposts;

namespace THOBOTTO.Moderation;

// A conversation moved to another channel: a message and those after it posted there under their authors' names and
// pictures (a post under a character's or another name keeps it), files carried over, then removed where they were.
// Reactions and threads stay behind; replies keep a line linking what they answered.
public sealed class MessageMover(
    AsMember asMember,
    SettingsStore settings,
    DeletionWitness witness,
    GatewayClient gateway,
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    [FromKeyedServices(nameof(MessageMover))] HttpClient http,
    TimeProvider time)
{
    public const int Most = 50;

    public async Task<string> MoveAsync(Actor actor, ulong fromId, ulong firstId, int count, ulong toId, string? reason)
    {
        if (!gateway.Cache.Guilds.TryGetValue(actor.GuildId, out var guild)
            || Channel(guild, fromId) is not { } from || Channel(guild, toId) is not { } to)
            return "Only between this server's text channels and threads.";
        if (fromId == toId)
            return "That's the channel they're in.";
        var withUsername = (await settings.GetAsync<RepostRules>(guild.Id, Reposter.ModuleId)).ShowUsername;
        var first = await rest.GetMessageAsync(fromId, firstId);
        var after = count > 1 ? await rest.GetMessagesAsync(fromId, new() { From = firstId, Direction = PaginationDirection.After, BatchSize = 100 }).Take(count - 1).ToListAsync() : [];
        var messages = after.Prepend(first).Where(m => m.Type is MessageType.Default or MessageType.Reply).OrderBy(m => m.Id).ToList();

        await rest.SendMessageAsync(toId, new()
        {
            Content = $"-# ↪ Moved from <#{fromId}> by <@{actor.Id}>{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}",
            AllowedMentions = AllowedMentionsProperties.None,
        });
        var moved = new List<ulong>();
        ulong? firstThere = null;
        foreach (var message in messages)
        {
            if (await CarryAsync(guild, to, message, withUsername) is not { } posted)
                continue;
            moved.Add(message.Id);
            firstThere ??= posted;
        }
        if (moved.Count == 0)
            return "None of them could be moved (only members' and the bot's posts for members are).";

        witness.Expect(moved, actor.Id);
        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(14) + TimeSpan.FromMinutes(5);
        var recent = moved.Where(id => Snowflake.Timestamp(id) > cutoff).ToList();
        foreach (var chunk in recent.Chunk(100))
        {
            if (chunk.Length == 1)
                await rest.DeleteMessageAsync(fromId, chunk[0], actor.Audit(reason));
            else
                await rest.DeleteMessagesAsync(fromId, chunk, actor.Audit(reason));
        }
        foreach (var old in moved.Except(recent))
            await rest.DeleteMessageAsync(fromId, old, actor.Audit(reason));

        var link = Notifier.Link(guild.Id, toId, firstThere!.Value);
        if ((await settings.GetAsync<ModRules>(guild.Id, CaseBook.ModuleId)).MoveNote)
            await rest.SendMessageAsync(fromId, new() { Content = $"-# {moved.Count} message{(moved.Count == 1 ? "" : "s")} moved to <#{toId}>: {link}", AllowedMentions = AllowedMentionsProperties.None });
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditEntries.Add(new() { GuildId = guild.Id, ActorId = actor.Id, Action = "messages.move", Details = $"{moved.Count} from {fromId} to {toId}{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return $"Moved {moved.Count} message{(moved.Count == 1 ? "" : "s")} to <#{toId}>: {link}";
    }

    private static TextGuildChannel? Channel(Guild guild, ulong id)
        => guild.Channels.GetValueOrDefault(id) as TextGuildChannel ?? guild.ActiveThreads.GetValueOrDefault(id);

    // The message posted there for its author; its id there, or null when it can't be (a bot's own, nothing to post).
    private async Task<ulong?> CarryAsync(Guild guild, TextGuildChannel to, RestMessage message, bool withUsername)
    {
        var authorId = await asMember.AuthorOfAsync(message);
        if (message.Author.IsBot && authorId == message.Author.Id)
            return null;
        var (name, avatar) = message.WebhookId is not null
            ? (message.Author.Username, (message.Author.GetAvatarUrl() ?? message.Author.DefaultAvatarUrl).ToString())
            : guild.Users.GetValueOrDefault(authorId) is { } member
                ? (AsMember.NameOf(member, withUsername), AsMember.AvatarOf(member))
                : (message.Author.GlobalName ?? message.Author.Username, (message.Author.GetAvatarUrl() ?? message.Author.DefaultAvatarUrl).ToString());
        var text = message.MessageReference is { Type: MessageReferenceType.Reply } reply
            ? $"-# ↪ {Notifier.Link(guild.Id, reply.ChannelId, reply.MessageId)}\n{message.Content}"
            : message.Content;
        var files = new List<MemoryStream>();
        try
        {
            var attachments = new List<AttachmentProperties>();
            foreach (var file in message.Attachments)
            {
                files.Add(new MemoryStream(await http.GetByteArrayAsync(file.Url)));
                attachments.Add(new AttachmentProperties(file.FileName, files[^1]) { Description = file.Description });
            }
            if (text.Length == 0 && attachments.Count == 0)
                return null;
            var (posted, _) = await asMember.PostForAsync(to, guild.Id, authorId, new()
            {
                Content = text.Length > 2000 ? text[..1999] + "…" : text,
                Username = name,
                AvatarUrl = avatar,
                Attachments = attachments,
                Flags = MessageFlags.SuppressNotifications,
            });
            return posted?.Id;
        }
        finally
        {
            foreach (var file in files)
                await file.DisposeAsync();
        }
    }
}

// Right-click a message, Apps, Move from here: it and the ones after it to another channel.
public sealed class MoveCommand : GuildCommandModule
{
    [MessageCommand("Move from here", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ManageMessages)]
    [RequirePermission(BotPermissions.ModMessages)]
    public InteractionCallbackProperties MoveAsync(RestMessage message)
        => InteractionCallback.Modal(new ModalProperties($"movemsgs:{message.ChannelId}:{message.Id}", "Move from here")
        {
            new LabelProperties("To which channel", new ChannelMenuProperties("to") { ChannelTypes = [ChannelType.TextGuildChannel, ChannelType.AnnouncementGuildChannel, ChannelType.PublicGuildThread, ChannelType.PrivateGuildThread] }),
            new LabelProperties($"How many, from this one on (at most {MessageMover.Most})", new TextInputProperties("count", TextInputStyle.Short) { Value = "1", MaxLength = 2 }),
            new LabelProperties("Why? (optional)", new TextInputProperties("why", TextInputStyle.Short) { Required = false, MaxLength = 200 }),
        });
}

public sealed class MoveModal(MessageMover mover) : GuildModalModule
{
    [ComponentInteraction("movemsgs")]
    public async Task MoveAsync(ulong channelId, ulong messageId)
    {
        var parts = Context.Components.OfType<Label>().Select(l => l.Component).ToList();
        var to = parts.OfType<ChannelMenu>().FirstOrDefault()?.SelectedValues?.FirstOrDefault()?.Id;
        var inputs = parts.OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        var count = int.TryParse(inputs.GetValueOrDefault("count"), out var n) ? Math.Clamp(n, 1, MessageMover.Most) : 1;
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = to is not { } toId ? "Pick a channel to move them to."
            : await mover.MoveAsync(new(GuildId, Context.User.Id, Context.User.Username), channelId, messageId, count, toId, inputs.GetValueOrDefault("why") is { Length: > 0 } why ? why : null);
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (result, AllowedMentionsProperties.None));
    }
}
