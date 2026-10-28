using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Modules;

namespace THOBOTTO.Events;

// Discord's own calendar and the bot's events kept in step: an event made in Discord becomes one of the bot's (in the
// channel the server named for that), with the people interested as coming; later interest and changes there follow
// here, and a cancellation there cancels here. The other way, the bot already lists its events in Discord's calendar
// and takes them down when cancelled; Discord has no way to mark someone interested on their behalf, so RSVPs made
// here don't show there.
public sealed class DiscordEventSync(EventBoard board, SettingsStore settings, ModuleState modules, GatewayClient gateway, RestClient rest, ILogger<DiscordEventSync> logger)
    : IGuildScheduledEventCreateGatewayHandler, IGuildScheduledEventUpdateGatewayHandler, IGuildScheduledEventDeleteGatewayHandler,
      IGuildScheduledEventUserAddGatewayHandler, IGuildScheduledEventUserRemoveGatewayHandler
{
    // The bot's own listing arrives here too, a moment before its event remembers the id.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    async ValueTask IGuildScheduledEventCreateGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        try
        {
            if (!await modules.IsEnabledAsync(arg.GuildId, EventBoard.ModuleId) || (await settings.GetAsync<EventRules>(arg.GuildId, EventBoard.ModuleId)).ImportChannelId is not { } channelId)
                return;
            await Task.Delay(Settle);
            if (await board.FindByDiscordEventAsync(arg.GuildId, arg.Id) is not null || arg.Status is not GuildScheduledEventStatus.Scheduled)
                return;
            var e = await board.CreateAsync(arg.GuildId, channelId, arg.CreatorId ?? gateway.Id, arg.Name, arg.Description, null, arg.ScheduledStartTime, null, wantsDiscordEvent: true, discordEventId: arg.Id);
            await foreach (var interested in rest.GetGuildScheduledEventUsersAsync(arg.GuildId, arg.Id))
            {
                if (interested.User.Id != gateway.Id)
                    await board.RsvpAsync(e.Id, interested.User.Id, RsvpStatuses.In);
            }
            logger.LogInformation("Discord event {DiscordEventId} in {GuildId} became event {EventId}", arg.Id, arg.GuildId, e.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Taking in Discord event {DiscordEventId} of {GuildId} failed", arg.Id, arg.GuildId);
        }
    }

    async ValueTask IGuildScheduledEventUpdateGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        try
        {
            if (await board.FindByDiscordEventAsync(arg.GuildId, arg.Id) is { } e)
                await board.FollowAsync(e, arg.Name, arg.Description, arg.ScheduledStartTime, arg.Status is GuildScheduledEventStatus.Canceled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Following Discord event {DiscordEventId} of {GuildId} failed", arg.Id, arg.GuildId);
        }
    }

    async ValueTask IGuildScheduledEventDeleteGatewayHandler.HandleAsync(GuildScheduledEvent arg)
    {
        try
        {
            if (await board.FindByDiscordEventAsync(arg.GuildId, arg.Id) is { } e)
                await board.FollowAsync(e, e.Title, e.Description, e.StartsAt ?? arg.ScheduledStartTime, cancelled: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cancelling after Discord event {DiscordEventId} of {GuildId} went failed", arg.Id, arg.GuildId);
        }
    }

    ValueTask IGuildScheduledEventUserAddGatewayHandler.HandleAsync(GuildScheduledEventUserEventArgs arg) => InterestAsync(arg, RsvpStatuses.In);

    ValueTask IGuildScheduledEventUserRemoveGatewayHandler.HandleAsync(GuildScheduledEventUserEventArgs arg) => InterestAsync(arg, RsvpStatuses.Out);

    private async ValueTask InterestAsync(GuildScheduledEventUserEventArgs arg, string status)
    {
        try
        {
            if (arg.UserId != gateway.Id && await board.FindByDiscordEventAsync(arg.GuildId, arg.GuildScheduledEventId) is { } e)
                await board.RsvpAsync(e.Id, arg.UserId, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Following interest in Discord event {DiscordEventId} of {GuildId} failed", arg.GuildScheduledEventId, arg.GuildId);
        }
    }
}
