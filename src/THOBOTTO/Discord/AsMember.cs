using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Members;

namespace THOBOTTO.Discord;

// A message the bot posted for a member (under their name, or its own answer to theirs), kept as theirs, since Discord
// shows the webhook or the bot as its author.
public sealed class MemberPost
{
    public ulong MessageId { get; init; }

    public ulong GuildId { get; init; }

    // The webhook's channel; a thread's parent when posted in a thread.
    public ulong ChannelId { get; init; }

    public ulong? ThreadId { get; init; }

    public ulong UserId { get; init; }

    // Posted under no one's name: only moderators may learn who wrote it.
    public bool Anonymous { get; init; }

    // Whether reactions to it count for them here (a copy from another server may not).
    public bool Credited { get; init; } = true;

    public DateTimeOffset PostedAt { get; init; }
}

// A post made for someone, as those following a channel's posts see it.
public sealed record PostedFor(TextGuildChannel Channel, RestMessage Message, ulong UserId, bool Anonymous);

// Posting under a member's name and avatar, through a webhook the bot keeps in the channel (a thread's in its parent),
// so what they post through the bot reads as theirs. Each post is kept with who wrote it, for whoever asks who a
// message is by (quotes, reports, points), and so they can edit it.
public sealed partial class AsMember(RestClient rest, GatewayClient gateway, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<AsMember> logger)
{
    private const string HookName = "Posts as members";

    private readonly ConcurrentDictionary<ulong, IncomingWebhook> _hooks = new();

    // Each post for someone that's theirs to say (not a copy of one, nor one moved).
    public event Func<PostedFor, Task>? Posted;
    private readonly SemaphoreSlim _making = new(1, 1);

    public static bool Can(Interaction interaction)
        => interaction is { Guild: not null, User: GuildInteractionUser member, Channel: TextGuildChannel channel }
            && interaction.AppPermissions.HasFlag(Permissions.ManageWebhooks)
            && member.Permissions.HasFlag(channel is GuildThread ? Permissions.SendMessagesInThreads : Permissions.SendMessages);

    // The name they go by here; with their username beside it when that's another, so a nickname can't pass for
    // someone else.
    public static string NameOf(GuildUser member, bool withUsername) => NameOf(member.Nickname ?? member.GlobalName ?? member.Username, member.Username, withUsername);

    public static string NameOf(string shown, string username, bool withUsername)
        => WebhookName(withUsername && !shown.Equals(username, StringComparison.OrdinalIgnoreCase) ? $"{shown} ({username})" : shown);

    public static string AvatarOf(GuildUser member) => (member.GetGuildAvatarUrl() ?? member.GetAvatarUrl() ?? member.DefaultAvatarUrl).ToString();

    // The post, else why not. Under the member's name and avatar unless the message names another.
    public async Task<(RestMessage? Posted, string? Problem)> PostAsync(TextGuildChannel channel, GuildUser member, bool withUsername, WebhookMessageProperties message)
    {
        message.Username ??= NameOf(member, withUsername);
        message.AvatarUrl ??= AvatarOf(member);
        return await PostForAsync(channel, member.GuildId, member.Id, message);
    }

    // Posted for someone under the name and picture the message carries (a moved message keeps its author's, even
    // one who left), and kept as theirs.
    public async Task<(RestMessage? Posted, string? Problem)> PostForAsync(TextGuildChannel channel, ulong guildId, ulong userId, WebhookMessageProperties message, bool anonymous = false, bool announce = true, bool credited = true)
    {
        var (channelId, threadId) = channel is GuildThread thread ? (thread.ParentId, (ulong?)thread.Id) : (channel.Id, null);
        message.Username = WebhookName(message.Username!);
        message.AllowedMentions ??= AllowedMentionsProperties.None;
        try
        {
            var posted = await OnHookAsync(channelId, hook => rest.ExecuteWebhookAsync(hook.Id, hook.Token!, message, wait: true, threadId: threadId));
            await using var db = await dbFactory.CreateDbContextAsync();
            db.MemberPosts.Add(new() { MessageId = posted!.Id, GuildId = guildId, ChannelId = channelId, ThreadId = threadId, UserId = userId, Anonymous = anonymous, Credited = credited, PostedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            if (announce)
            {
                foreach (var handler in Posted?.GetInvocationList().Cast<Func<PostedFor, Task>>() ?? [])
                {
                    try
                    {
                        await handler(new(channel, posted, userId, anonymous));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Following post {Message} failed", posted.Id);
                    }
                }
            }
            return (posted, null);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Couldn't post for {User} in {Channel}: {Message}", userId, channelId, ex.Message);
            return (null, ex.Message);
        }
    }

    public async Task<MemberPost?> FindAsync(ulong messageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MemberPosts.AsNoTracking().FirstOrDefaultAsync(p => p.MessageId == messageId);
    }

    // An answer the bot itself posted to a member's message (a link that previews), counted as theirs.
    public async Task CreditAsync(RestMessage answer, ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.MemberPosts.Add(new() { MessageId = answer.Id, GuildId = guildId, ChannelId = answer.ChannelId, UserId = userId, PostedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    // Whom reactions to a message count for (points, the hall of fame): its author; for one the bot posted for a member,
    // that member; no one for an anonymous one, or a copy whose writer doesn't count here.
    public async Task<ulong?> CreditedAsync(ulong guildId, ulong messageId, ulong? authorId)
    {
        if (authorId is { } known && known != gateway.Id && gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.ContainsKey(known))
            return known;
        return await FindAsync(messageId) switch
        {
            { Anonymous: true } or { Credited: false } => null,
            { } post => post.UserId,
            null => authorId,
        };
    }

    // Who wrote a message, as anyone may know it (quotes, reports): for one the bot posted for a member, that member;
    // an anonymous one stays the webhook's.
    public async Task<ulong> AuthorOfAsync(RestMessage message)
        => ForSomeone(message) && await FindAsync(message.Id) is { Anonymous: false } post ? post.UserId : message.Author.Id;

    // Who wrote it, anonymous or not: for moderators acting on it.
    public async Task<ulong> WhoWroteAsync(RestMessage message)
        => ForSomeone(message) && await FindAsync(message.Id) is { } post ? post.UserId : message.Author.Id;

    private bool ForSomeone(RestMessage message) => message.WebhookId is not null || message.Author.Id == gateway.Id;

    // Null once changed, else why not.
    public Task<string?> EditAsync(MemberPost post, string content)
        => ChangeAsync(post, hook => rest.ModifyWebhookMessageAsync(hook.Id, hook.Token!, post.MessageId, m => (m.Content, m.AllowedMentions) = (content, AllowedMentionsProperties.None), post.ThreadId));

    public async Task<string?> DeleteAsync(MemberPost post)
    {
        var problem = await ChangeAsync(post, async hook =>
        {
            await rest.DeleteWebhookMessageAsync(hook.Id, hook.Token!, post.MessageId, post.ThreadId);
            return true;
        });
        if (problem is null)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.MemberPosts.Where(p => p.MessageId == post.MessageId).ExecuteDeleteAsync();
        }
        return problem;
    }

    private async Task<string?> ChangeAsync<T>(MemberPost post, Func<IncomingWebhook, Task<T>> change)
    {
        try
        {
            await change(await HookAsync(post.ChannelId));
            return null;
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return "It's gone, or the webhook it was posted through was removed.";
        }
        catch (RestException ex)
        {
            logger.LogWarning("Couldn't change post {Message} in {Channel}: {Problem}", post.MessageId, post.ChannelId, ex.Message);
            return ex.Message;
        }
    }

    private async Task<T> OnHookAsync<T>(ulong channelId, Func<IncomingWebhook, Task<T>> use)
    {
        try
        {
            return await use(await HookAsync(channelId));
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Its webhook was deleted: made again.
            _hooks.TryRemove(channelId, out _);
            return await use(await HookAsync(channelId));
        }
    }

    private async Task<IncomingWebhook> HookAsync(ulong channelId)
    {
        if (_hooks.TryGetValue(channelId, out var known))
            return known;
        await _making.WaitAsync();
        try
        {
            if (_hooks.TryGetValue(channelId, out known))
                return known;
            var hook = (await rest.GetChannelWebhooksAsync(channelId)).OfType<IncomingWebhook>()
                .FirstOrDefault(h => h.ApplicationId == gateway.Id && h.Name == HookName && h.Token is not null)
                ?? await rest.CreateWebhookAsync(channelId, new WebhookProperties(HookName));
            return _hooks[channelId] = hook;
        }
        finally
        {
            _making.Release();
        }
    }

    // Discord refuses webhook names with "discord" or "clyde" in them, and longer than 80.
    public static string WebhookName(string name)
    {
        var named = Refused().Replace(name, m => $"{m.Value[0]}​{m.Value[1..]}");
        return named.Length <= 80 ? named : named[..80];
    }

    [GeneratedRegex("discord|clyde", RegexOptions.IgnoreCase)]
    private static partial Regex Refused();
}

// The messages a member posted through the bot under their name.
public sealed class MemberPostsData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Posts the bot made for you";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MemberPosts.LongCountAsync(p => p.GuildId == guildId && p.UserId == userId);
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        await foreach (var post in db.MemberPosts.AsNoTracking().Where(p => p.GuildId == guildId && p.UserId == userId).OrderBy(p => p.PostedAt).AsAsyncEnumerable())
        {
            json.WriteStartObject();
            json.WriteString("message", $"https://discord.com/channels/{guildId}/{post.ThreadId ?? post.ChannelId}/{post.MessageId}");
            json.WriteString("postedAt", post.PostedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }
}
