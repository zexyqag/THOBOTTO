using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Stats;

// Posts Wrapped on its own: last month's on the 1st, the year's on December 31st (with each helper's
// in its own voice, posted by the helper, and a DM with their own to members who asked). Times are the
// server's; a post the bot missed while down follows within a few days.
public sealed class WrappedPoster(
    GatewayClient gateway,
    RestClient rest,
    ModuleState modules,
    SettingsStore settings,
    WrappedService wrapped,
    HelperFleet fleet,
    Notifier notifier,
    TimeZones zones,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<WrappedPoster> logger) : BackgroundService
{
    public const string ModuleId = "wrapped";

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    private const int CatchUpDays = 3;
    private const int Voices = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var guild in gateway.Cache.Guilds.Values.ToList())
            {
                try
                {
                    await CheckAsync(guild);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Posting Wrapped in {GuildId} failed", guild.Id);
                }
            }
        }
    }

    private async Task CheckAsync(Guild guild)
    {
        if (!await modules.IsEnabledAsync(guild.Id, ModuleId))
            return;
        var rules = await settings.GetAsync<WrappedRules>(guild.Id, ModuleId);
        if (rules.ChannelId is not { } channelId || !guild.Channels.ContainsKey(channelId))
            return;

        var (zone, _) = await zones.ForAsync(guild.Id, 0);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow()).InZone(zone);
        if (rules.Monthly && now.Day <= CatchUpDays)
        {
            var month = now.Date.PlusMonths(-1);
            if (await ClaimAsync(guild.Id, $"month:{month:yyyy-MM}"))
                await PostAsync(guild, channelId, WrappedPeriod.LastMonth, $"📅 {month.ToString("MMMM", null)}, wrapped up!", null);
        }
        // From noon on New Year's Eve; else in the first days of January, for the year before.
        var yearly = now.Month == 12 && now.Day == 31 && now.Hour >= 12 ? (Year: now.Year, Period: WrappedPeriod.ThisYear)
            : now.Month == 1 && now.Day <= CatchUpDays ? (Year: now.Year - 1, Period: WrappedPeriod.LastYear)
            : default;
        if (rules.Yearly && yearly.Year > 0 && await ClaimAsync(guild.Id, $"year:{yearly.Year}"))
            await PostAsync(guild, channelId, yearly.Period, $"🎁 {yearly.Year}, wrapped! Here's how this server sounded and talked.", $"your {yearly.Year}, wrapped 🎁");
    }

    // The year's also gets the helpers' and the members' DMs (their heading given).
    private async Task PostAsync(Guild guild, ulong channelId, WrappedPeriod period, string heading, string? yours)
    {
        static string Mention(ulong id) => $"<@{id}>";
        var cards = await wrapped.ServerAsync(guild, period, Mention);
        // A quiet month isn't worth a post.
        if (cards.All(c => c.Facts.Count == 0))
            return;
        await rest.SendMessageAsync(channelId, Message(heading, cards));
        if (yours is null)
            return;

        // Each helper voice that played, from the helper itself where it may post.
        foreach (var (key, name) in (await wrapped.VoicesAsync(guild.Id)).Take(Voices))
        {
            if (await wrapped.VoiceAsync(guild, key, period, Mention) is not { Facts.Count: > 0 } card)
                continue;
            var helper = key.StartsWith("h:") && ulong.TryParse(key[2..], out var id) ? fleet.Helpers.FirstOrDefault(h => h.UserId == id)
                : (await PersonalityWearersAsync(guild.Id, key[2..])).FirstOrDefault();
            await SendAsHelperAsync(helper, channelId, Message("", [card]));
        }

        // Their own Wrapped, privately, to those who asked for it.
        foreach (var userId in await notifier.SubscribersAsync(guild.Id, NotificationTopics.WrappedYou))
        {
            try
            {
                var mine = await wrapped.MemberAsync(guild, userId, "Your", period, Mention);
                var dm = await rest.GetDMChannelAsync(userId);
                await rest.SendMessageAsync(dm.Id, Message($"**{guild.Name}**: {yours}", mine));
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                logger.LogDebug("Couldn't DM {UserId} their Wrapped: {Message}", userId, ex.Message);
            }
        }
    }

    private async Task SendAsHelperAsync(HelperBot? helper, ulong channelId, MessageProperties message)
    {
        if (helper is not null)
        {
            try
            {
                await helper.Gateway.Rest.SendMessageAsync(channelId, message);
                return;
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
            }
        }
        await rest.SendMessageAsync(channelId, message);
    }

    private async Task<IReadOnlyList<HelperBot>> PersonalityWearersAsync(ulong guildId, string personality)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var wearers = await db.HelperAssignments.Where(a => a.GuildId == guildId)
            .Join(db.Personalities.Where(p => p.Name == personality), a => a.PersonalityId, p => p.Id, (a, _) => a.HelperId)
            .ToListAsync();
        return fleet.Helpers.Where(h => wearers.Contains(h.UserId) && h.InGuild(guildId)).ToList();
    }

    // True the first time: this one is to be posted.
    private async Task<bool> ClaimAsync(ulong guildId, string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WrappedPosts.AnyAsync(p => p.GuildId == guildId && p.Key == key))
            return false;
        db.WrappedPosts.Add(new() { GuildId = guildId, Key = key, At = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return true;
    }

    private static MessageProperties Message(string content, IReadOnlyList<WrappedCard> cards) => new()
    {
        Content = content,
        Embeds = WrappedEmbeds.Embeds(cards),
        AllowedMentions = AllowedMentionsProperties.None,
    };
}
