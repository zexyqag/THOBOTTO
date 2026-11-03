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

// A message the bot posted under a member's name: who wrote it, since Discord shows its webhook as the author.
public sealed class MemberPost
{
    public ulong MessageId { get; init; }

    public ulong GuildId { get; init; }

    // The webhook's channel; a thread's parent when posted in a thread.
    public ulong ChannelId { get; init; }

    public ulong? ThreadId { get; init; }

    public ulong UserId { get; init; }

    public DateTimeOffset PostedAt { get; init; }
}

// Posting under a member's name and avatar, through a webhook the bot keeps in the channel (a thread's in its parent),
// so what they post through the bot reads as theirs. Each post is kept with who wrote it, for whoever asks who a
// message is by (quotes, reports, points), and so they can edit it.
public sealed partial class AsMember(RestClient rest, GatewayClient gateway, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<AsMember> logger)
{
    private const string HookName = "Posts as members";

    private readonly ConcurrentDictionary<ulong, IncomingWebhook> _hooks = new();
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

    // The post, else why not.
    public async Task<(RestMessage? Posted, string? Problem)> PostAsync(TextGuildChannel channel, GuildUser member, bool withUsername, WebhookMessageProperties message)
    {
        var (channelId, threadId) = channel is GuildThread thread ? (thread.ParentId, (ulong?)thread.Id) : (channel.Id, null);
        message.Username = NameOf(member, withUsername);
        message.AvatarUrl = (member.GetGuildAvatarUrl() ?? member.GetAvatarUrl() ?? member.DefaultAvatarUrl).ToString();
        message.AllowedMentions ??= AllowedMentionsProperties.None;
        try
        {
            var posted = await OnHookAsync(channelId, hook => rest.ExecuteWebhookAsync(hook.Id, hook.Token!, message, wait: true, threadId: threadId));
            await using var db = await dbFactory.CreateDbContextAsync();
            db.MemberPosts.Add(new() { MessageId = posted!.Id, GuildId = member.GuildId, ChannelId = channelId, ThreadId = threadId, UserId = member.Id, PostedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            return (posted, null);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Couldn't post as {User} in {Channel}: {Message}", member.Id, channelId, ex.Message);
            return (null, ex.Message);
        }
    }

    public async Task<MemberPost?> FindAsync(ulong messageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MemberPosts.AsNoTracking().FirstOrDefaultAsync(p => p.MessageId == messageId);
    }

    // Who wrote a message: for one posted under a member's name, that member.
    public async Task<ulong?> AuthorOfAsync(ulong guildId, ulong messageId, ulong? authorId)
    {
        if (authorId is { } known && gateway.Cache.Guilds.TryGetValue(guildId, out var guild) && guild.Users.ContainsKey(known))
            return known;
        return (await FindAsync(messageId))?.UserId ?? authorId;
    }

    public async Task<ulong> AuthorOfAsync(RestMessage message)
        => message.WebhookId is null ? message.Author.Id : (await FindAsync(message.Id))?.UserId ?? message.Author.Id;

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
    public string Name => "Posts under your name";

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
