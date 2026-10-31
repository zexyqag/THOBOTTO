using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Live;

// The Twitch channels servers follow, asked about once a minute (one ask for up to 100 channels): what's live goes to
// LiveWatch. When Twitch doesn't answer, nothing is taken as over.
public sealed class TwitchWatch(IDbContextFactory<BotDbContext> dbFactory, TwitchClient twitch, LiveWatch live, ModuleState modules, TimeProvider time, ILogger<TwitchWatch> logger)
    : BackgroundService
{
    public const int MostFollowed = 50;
    public const string TwitchNotSetUp = "Twitch isn't set up: the bot's owner sets a Twitch application's id and secret on the panel's Integrations page.";
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<TwitchChannel>> FollowedAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TwitchChannels.AsNoTracking().Where(c => c.GuildId == guildId).OrderBy(c => c.Name).ToListAsync();
    }

    // The channel followed, or why not.
    public async Task<(TwitchChannel? Followed, string? Problem)> FollowAsync(ulong guildId, string channel, ulong actorId)
    {
        if (!twitch.Configured)
            return (null, TwitchNotSetUp);
        if (TwitchClient.Login(channel) is null)
            return (null, "That isn't a Twitch channel's name or link.");
        TwitchUser? user;
        try
        {
            user = await twitch.FindAsync(channel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Looking up the Twitch channel {Channel} failed: {Message}", channel, ex.Message);
            return (null, "Twitch didn't answer; try again in a moment.");
        }
        if (user is null)
            return (null, "There's no such Twitch channel.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.TwitchChannels.AnyAsync(c => c.GuildId == guildId && c.TwitchId == user.Id))
            return (null, $"**{user.Name}** is followed already.");
        if (await db.TwitchChannels.CountAsync(c => c.GuildId == guildId) >= MostFollowed)
            return (null, $"This server follows {MostFollowed} Twitch channels, the most it takes.");
        var followed = new TwitchChannel { GuildId = guildId, TwitchId = user.Id, Login = user.Login, Name = user.Name, AddedBy = actorId, CreatedAt = time.GetUtcNow() };
        db.TwitchChannels.Add(followed);
        await db.SaveChangesAsync();
        return (followed, null);
    }

    public async Task<TwitchChannel?> UnfollowAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.TwitchChannels.FirstOrDefaultAsync(c => c.Id == id && c.GuildId == guildId) is not { } channel)
            return null;
        db.TwitchChannels.Remove(channel);
        await db.SaveChangesAsync();
        // A stream of it still open ends with the next look, as it's no longer asked about.
        await live.TwitchSeenAsync(guildId, [channel], new Dictionary<string, TwitchLive>());
        return channel;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            if (!twitch.Configured)
                continue;
            try
            {
                await LookAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Asking Twitch who's live failed: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task LookAsync(CancellationToken ct)
    {
        List<TwitchChannel> followed;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            followed = await db.TwitchChannels.AsNoTracking().ToListAsync(ct);
        var guilds = new List<IGrouping<ulong, TwitchChannel>>();
        foreach (var guild in followed.GroupBy(c => c.GuildId))
        {
            if (await modules.IsEnabledAsync(guild.Key, LiveWatch.ModuleId))
                guilds.Add(guild);
        }
        if (guilds.Count == 0)
            return;
        var streams = await twitch.LiveAsync([.. guilds.SelectMany(g => g).Select(c => c.TwitchId).Distinct()]);
        foreach (var guild in guilds)
            await live.TwitchSeenAsync(guild.Key, [.. guild], streams);
    }
}
