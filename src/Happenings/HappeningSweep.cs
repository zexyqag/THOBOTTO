using NetCord.Gateway;

namespace THOBOTTO.Happenings;

// Now and then, archives the forum's records whose time to talk about them is up, in the servers the bot is in.
public sealed class HappeningSweep(GatewayClient gateway, HappeningForum forum, TimeProvider time, ILogger<HappeningSweep> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var guildId in gateway.Cache.Guilds.Keys.ToList())
            {
                try
                {
                    await forum.CloseDueAsync(guildId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Closing the forum's records in {GuildId} failed", guildId);
                }
            }
        }
    }
}
