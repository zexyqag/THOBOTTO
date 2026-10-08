using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Moderation;

// Ends what was given for a while (bans, locks, mutes, roles); timeouts Discord ends itself, and
// their cases are only marked over.
public sealed class ModTimers(CaseBook cases, ModActions actions, GatewayClient gateway, TimeProvider time, ILogger<ModTimers> logger) : BackgroundService
{
    private static readonly TimeSpan Sweep = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Sweep, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var c in await cases.DueAsync())
            {
                try
                {
                    if (c.Type != CaseTypes.Timeout && gateway.Cache.User is { } bot)
                        await actions.LiftAsync(new(c.GuildId, bot.Id, bot.Username), c, $"case #{c.Number} ran out");
                    else
                        await cases.EndAsync(c.GuildId, c.TargetId, c.Type);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ending case {Number} in {GuildId} failed", c.Number, c.GuildId);
                }
            }
        }
    }
}

// Bans, kicks and timeouts done in Discord itself (its menus, or other bots) become cases too, so
// the history is complete. The bot's own actions are recorded where they're made.
public sealed class ExternalModeration(CaseBook cases, ModuleState modules, GatewayClient gateway, TimeProvider time) : IGuildAuditLogEntryCreateGatewayHandler
{
    public async ValueTask HandleAsync(AuditLogEntry entry)
    {
        if (entry.UserId is not { } moderatorId || moderatorId == gateway.Cache.User?.Id || entry.TargetId is not { } targetId
            || !await modules.IsEnabledAsync(entry.GuildId, CaseBook.ModuleId))
            return;

        var actor = new Actor(entry.GuildId, moderatorId, "");
        var now = time.GetUtcNow();
        switch (entry.ActionType)
        {
            case AuditLogEvent.GuildUserKick:
                await cases.OpenAsync(actor.Case(CaseTypes.Kick, targetId, entry.Reason, now), dm: false);
                break;
            case AuditLogEvent.GuildUserBanAdd:
                await cases.EndAsync(entry.GuildId, targetId, CaseTypes.Ban);
                await cases.OpenAsync(actor.Case(CaseTypes.Ban, targetId, entry.Reason, now), dm: false);
                break;
            case AuditLogEvent.GuildUserBanRemove:
                await cases.EndAsync(entry.GuildId, targetId, CaseTypes.Ban);
                await cases.OpenAsync(actor.Case(CaseTypes.Unban, targetId, entry.Reason, now), dm: false);
                break;
            case AuditLogEvent.GuildUserUpdate when entry.Changes.TryGetValue("communication_disabled_until", out var change):
                await cases.EndAsync(entry.GuildId, targetId, CaseTypes.Timeout);
                var until = change.WithValues<DateTimeOffset?>().NewValue;
                var c = actor.Case(until > now ? CaseTypes.Timeout : CaseTypes.Untimeout, targetId, entry.Reason, now);
                c.EndsAt = until > now ? until : null;
                await cases.OpenAsync(c, dm: false);
                break;
        }
    }
}
