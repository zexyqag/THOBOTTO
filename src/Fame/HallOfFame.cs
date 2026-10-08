using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using Npgsql;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Fame;

// Counts the different people reacting to each recent message; at the threshold the message is
// forwarded to the showcase channel (a forward keeps its attachments, which plain links don't)
// and its author gets a bonus.
public sealed class HallOfFame(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    TimeProvider time,
    ILogger<HallOfFame> logger)
{
    public const string ModuleId = "hall-of-fame";

    private const string UniqueViolation = "23505";

    // Discord's epoch, for reading a message's age from its id.
    private static readonly DateTimeOffset DiscordEpoch = new(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string EmojiKey(MessageReactionEmoji emoji) => emoji.Id?.ToString() ?? emoji.Name ?? "";

    public static DateTimeOffset CreatedAt(ulong messageId) => DiscordEpoch.AddMilliseconds(messageId >> 22);

    public async Task OnReactionAddedAsync(ulong guildId, ulong channelId, ulong messageId, ulong authorId, ulong reactorId, string emoji)
    {
        if (reactorId == authorId || !await modules.IsEnabledAsync(guildId, ModuleId))
            return;

        var rules = await settings.GetAsync<FameRules>(guildId, ModuleId);
        var now = time.GetUtcNow();
        if (rules.ShowcaseChannelId is not { } showcase
            || channelId == showcase
            || rules.ExcludedChannelIds.Contains(channelId)
            || (rules.Emojis.Count > 0 && !rules.Emojis.Contains(emoji))
            || now - CreatedAt(messageId) > TimeSpan.FromDays(rules.MaxAgeDays))
            return;

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlAsync($"""
            insert into fame_reactions (message_id, reactor_id, emoji, guild_id, created_at)
            values ({messageId}, {reactorId}, {emoji}, {guildId}, {now})
            on conflict do nothing
            """);

        var reactors = await db.FameReactions.Where(r => r.MessageId == messageId).Select(r => r.ReactorId).Distinct().CountAsync();
        if (reactors < rules.Threshold || await db.FameEntries.AnyAsync(e => e.MessageId == messageId))
            return;

        var entry = new FameEntry { MessageId = messageId, GuildId = guildId, ChannelId = channelId, AuthorId = authorId, Reactors = reactors, InductedAt = now };
        db.FameEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Another reaction got there first.
            return;
        }

        await InductAsync(db, entry, showcase, rules);
    }

    public async Task OnReactionRemovedAsync(ulong messageId, ulong reactorId, string emoji)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.FameReactions.Where(r => r.MessageId == messageId && r.ReactorId == reactorId && r.Emoji == emoji).ExecuteDeleteAsync();
    }

    private async Task InductAsync(BotDbContext db, FameEntry entry, ulong showcase, FameRules rules)
    {
        try
        {
            var message = await rest.GetMessageAsync(entry.ChannelId, entry.MessageId);
            if (message.Author.IsBot)
                return;

            await rest.SendMessageAsync(showcase, new()
            {
                Content = $"⭐ <@{entry.AuthorId}> in <#{entry.ChannelId}> · {entry.Reactors} {(entry.Reactors == 1 ? "person" : "people")} reacted",
                AllowedMentions = AllowedMentionsProperties.None,
            });
            var forward = await rest.SendMessageAsync(showcase, new()
            {
                MessageReference = MessageReferenceProperties.Forward(entry.ChannelId, entry.MessageId, failIfNotExists: true),
            });

            entry.ShowcaseMessageId = forward.Id;
            await db.SaveChangesAsync();
        }
        catch (RestException ex)
        {
            logger.LogWarning(ex, "Posting message {MessageId} to the hall of fame failed", entry.MessageId);
            return;
        }

        if (rules.Bonus > 0 && await points.ChargesAsync(entry.GuildId))
            await points.AwardAsync(entry.GuildId, entry.AuthorId, rules.Bonus, PointEntryKinds.Fame, $"hall of fame {entry.MessageId}");
    }
}
