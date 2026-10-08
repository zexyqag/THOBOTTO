using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;

namespace THOBOTTO.Notifications;

// Sends DMs to members who opted in to a topic. Everything else (role pings, channel messages)
// stays with the modules; this only adds the DM for those who asked for one.
public sealed class Notifier(
    RestClient rest,
    GatewayClient gateway,
    IDbContextFactory<BotDbContext> dbFactory,
    IEnumerable<INotificationTopicSource> sources,
    ILogger<Notifier> logger)
{
    public async Task<IReadOnlyList<NotificationTopic>> TopicsAsync(ulong guildId)
    {
        var topics = NotificationTopics.Fixed.ToList();
        foreach (var source in sources)
            topics.AddRange(await source.TopicsAsync(guildId));
        return topics;
    }

    public async Task<IReadOnlySet<string>> SubscriptionsAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.NotificationPreferences.Where(p => p.GuildId == guildId && p.UserId == userId).Select(p => p.Topic).ToHashSetAsync();
    }

    public async Task SetAsync(ulong guildId, ulong userId, string topic, bool dm)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.NotificationPreferences.FindAsync(guildId, userId, topic);
        if (dm && row is null)
            db.NotificationPreferences.Add(new() { GuildId = guildId, UserId = userId, Topic = topic });
        else if (!dm && row is not null)
            db.NotificationPreferences.Remove(row);
        await db.SaveChangesAsync();
    }

    // DMs those of the given members who opted in to the topic.
    public async Task NotifyAsync(ulong guildId, string topic, IEnumerable<ulong> userIds, string text, string? link = null)
    {
        var candidates = userIds.Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        var subscribed = await db.NotificationPreferences
            .Where(p => p.GuildId == guildId && p.Topic == topic && candidates.Contains(p.UserId))
            .Select(p => p.UserId)
            .ToListAsync();
        await SendAsync(guildId, subscribed, text, link);
    }

    // DMs everyone who opted in to the topic (e.g. a new session for a game they follow).
    public async Task NotifySubscribersAsync(ulong guildId, string topic, string text, string? link = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var subscribed = await db.NotificationPreferences.Where(p => p.GuildId == guildId && p.Topic == topic).Select(p => p.UserId).ToListAsync();
        await SendAsync(guildId, subscribed, text, link);
    }

    private async Task SendAsync(ulong guildId, IReadOnlyList<ulong> userIds, string text, string? link)
    {
        var server = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.Name : "a server";
        var content = $"**{server}**: {text}{(link is null ? "" : $"\n{link}")}";

        foreach (var userId in userIds)
        {
            try
            {
                var dm = await rest.GetDMChannelAsync(userId);
                await rest.SendMessageAsync(dm.Id, new() { Content = content, AllowedMentions = AllowedMentionsProperties.None });
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                // DMs closed, or no shared server any more.
                logger.LogDebug("Couldn't DM {UserId}: {Message}", userId, ex.Message);
            }
        }
    }

    public static string Link(ulong guildId, ulong channelId, ulong? messageId = null)
        => $"https://discord.com/channels/{guildId}/{channelId}{(messageId is { } m ? $"/{m}" : "")}";
}
