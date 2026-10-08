using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// Pages back through the history of every channel and thread of guilds with a running backfill,
// newest first, handing each message to the Archiver like a live one. A cursor per channel is
// saved after every page, so a restart carries on where it stopped.
public sealed class Backfiller(
    RestClient rest,
    Archiver archiver,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    TimeProvider time,
    ILogger<Backfiller> logger) : BackgroundService
{
    private const int PageSize = 100;

    // Let the writer catch up before fetching more.
    private const int MaxPending = 1000;

    private readonly SemaphoreSlim _wake = new(0, 1);

    public async Task StartAsync(ulong guildId, bool restart)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (restart)
            await db.BackfillChannels.Where(c => c.GuildId == guildId).ExecuteDeleteAsync();

        var run = await db.BackfillRuns.FindAsync(guildId);
        if (run is null)
            db.BackfillRuns.Add(run = new() { GuildId = guildId });
        run.Running = true;
        run.StartedAt = time.GetUtcNow();
        run.FinishedAt = null;
        await db.SaveChangesAsync();

        Wake();
    }

    public async Task PauseAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.BackfillRuns.Where(r => r.GuildId == guildId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Running, false));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backfill failed; retrying shortly");
            }

            await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        List<BackfillRun> runs;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            runs = await db.BackfillRuns.Where(r => r.Running).ToListAsync(ct);

        foreach (var run in runs)
        {
            if (!await modules.IsEnabledAsync(run.GuildId, Archiver.ModuleId))
                continue;

            await DiscoverAsync(run.GuildId, ct);
            while (await NextChannelAsync(run.GuildId, ct) is { } channel)
            {
                if (!await IsRunningAsync(run.GuildId, ct))
                    break;
                await FetchChannelAsync(channel, ct);
            }

            if (await IsRunningAsync(run.GuildId, ct) && await NextChannelAsync(run.GuildId, ct) is null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.BackfillRuns.Where(r => r.GuildId == run.GuildId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Running, false).SetProperty(r => r.FinishedAt, time.GetUtcNow()), ct);
                logger.LogInformation("Backfill of guild {GuildId} finished", run.GuildId);
            }
        }
    }

    // Adds every channel and thread that holds messages and isn't known yet.
    private async Task DiscoverAsync(ulong guildId, CancellationToken ct)
    {
        var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);
        var found = new Dictionary<ulong, string>();

        var channels = await rest.GetGuildChannelsAsync(guildId, cancellationToken: ct);
        foreach (var channel in channels)
        {
            if (channel is TextGuildChannel)
                found[channel.Id] = channel.Name;
            // Voice channels have a text chat but no threads.
            if (channel is (TextGuildChannel and not IVoiceGuildChannel) or ForumGuildChannel or MediaForumGuildChannel)
                await AddArchivedThreadsAsync(channel.Id, found, ct);
        }

        foreach (var thread in await rest.GetActiveGuildThreadsAsync(guildId, cancellationToken: ct))
            found[thread.Id] = thread.Name;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.BackfillChannels.Where(c => c.GuildId == guildId).Select(c => c.ChannelId).ToHashSetAsync(ct);
        foreach (var (id, name) in found.Where(f => !known.Contains(f.Key) && !rules.ExcludedChannelIds.Contains(f.Key)))
            db.BackfillChannels.Add(new() { ChannelId = id, GuildId = guildId, Name = name });
        await db.SaveChangesAsync(ct);
    }

    private async Task AddArchivedThreadsAsync(ulong channelId, Dictionary<ulong, string> found, CancellationToken ct)
    {
        try
        {
            await foreach (var thread in rest.GetPublicArchivedGuildThreadsAsync(channelId).WithCancellation(ct))
                found[thread.Id] = thread.Name;
            await foreach (var thread in rest.GetPrivateArchivedGuildThreadsAsync(channelId).WithCancellation(ct))
                found[thread.Id] = thread.Name;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // No access to this channel's (private) threads, or a channel type without threads.
        }
    }

    private async Task<BackfillChannel?> NextChannelAsync(ulong guildId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BackfillChannels
            .Where(c => c.GuildId == guildId && !c.Done)
            .OrderBy(c => c.ChannelId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> IsRunningAsync(ulong guildId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.BackfillRuns.AnyAsync(r => r.GuildId == guildId && r.Running, ct);
    }

    // One channel, page by page, until its first message. Stops early when the run is paused.
    private async Task FetchChannelAsync(BackfillChannel channel, CancellationToken ct)
    {
        while (true)
        {
            while (archiver.Pending > MaxPending)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

            List<RestMessage> page;
            try
            {
                page = await rest.GetMessagesAsync(channel.ChannelId, new() { From = channel.Cursor, Direction = PaginationDirection.Before, BatchSize = PageSize })
                    .Take(PageSize)
                    .ToListAsync(ct);
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                await SaveAsync(channel, done: true, problem: ex.StatusCode == HttpStatusCode.Forbidden ? "no access" : "gone", ct);
                return;
            }

            foreach (var message in page)
                archiver.OnFetched(channel.GuildId, message);

            channel.Fetched += page.Count;
            if (page.Count > 0)
                channel.Cursor = page.Min(m => m.Id);

            var done = page.Count < PageSize;
            await SaveAsync(channel, done, problem: null, ct);
            if (done || !await IsRunningAsync(channel.GuildId, ct))
                return;
        }
    }

    private async Task SaveAsync(BackfillChannel channel, bool done, string? problem, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.BackfillChannels.Where(c => c.ChannelId == channel.ChannelId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Cursor, channel.Cursor)
                .SetProperty(c => c.Fetched, channel.Fetched)
                .SetProperty(c => c.Done, done)
                .SetProperty(c => c.Problem, problem), ct);
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
