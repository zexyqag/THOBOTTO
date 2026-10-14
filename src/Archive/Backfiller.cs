using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

// Fetches history through the Archiver, which takes each message like a live one and reconciles the ones it
// has. A backfill pages back through every channel and thread of a guild, newest first, with a cursor per
// channel saved after every page, so a restart carries on where it stopped. A catch-up (at a start, on a
// resume) pages forward in every channel the archive has messages of, from the last page it holds, so what
// happened while the bot was away is caught: new messages, edits, deletions, reactions.
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

    // Let the writer, and Discord's answers about who reacted, catch up before fetching more.
    private const int MaxPending = 1000;
    private const int MaxChecks = 200;

    private readonly SemaphoreSlim _wake = new(0, 1);

    // Servers to catch up on, each with the channels and threads live in it now.
    private readonly Dictionary<ulong, HashSet<ulong>> _catchUps = [];

    public async Task StartAsync(Guild guild, bool restart)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (restart)
            await db.BackfillChannels.Where(c => c.GuildId == guild.Id).ExecuteDeleteAsync();

        var run = await db.BackfillRuns.FindAsync(guild.Id);
        if (run is null)
            db.BackfillRuns.Add(run = new() { GuildId = guild.Id });
        run.Name = guild.Name;
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

    // The channels and threads a message can be fetched from now.
    public static IEnumerable<ulong> LiveChannels(Guild guild) => guild.Channels.Keys.Concat(guild.ActiveThreads.Keys);

    public void CatchUp(ulong guildId, IEnumerable<ulong> liveChannelIds)
    {
        lock (_catchUps)
        {
            if (!_catchUps.TryGetValue(guildId, out var ids))
                _catchUps[guildId] = ids = [];
            ids.UnionWith(liveChannelIds);
        }
        Wake();
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
        await CatchUpAsync(ct);

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
                // A catch-up is small and about now; it doesn't wait for a backfill of years.
                await CatchUpAsync(ct);
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

    private async Task CatchUpAsync(CancellationToken ct)
    {
        while (TakeCatchUp() is { } request)
        {
            var (guildId, live) = request;
            if (!await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
                continue;
            var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);

            // Each channel from the last page the archive holds, so edits and deletions in it are seen too.
            var starts = new List<(ulong ChannelId, ulong Start)>();
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var archived = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().ToListAsync(ct);
                foreach (var channelId in archived.Where(id => live.Contains(id) && !rules.ExcludedChannelIds.Contains(id)))
                {
                    var newest = await db.ArchivedMessages.Where(m => m.ChannelId == channelId).OrderByDescending(m => m.Id).Select(m => m.Id).Take(PageSize).ToListAsync(ct);
                    starts.Add((channelId, newest[^1]));
                }
            }

            var fetched = 0;
            foreach (var (channelId, start) in starts)
                fetched += await FetchForwardAsync(guildId, channelId, start, ct);
            logger.LogInformation("Catch-up of {GuildId}: {Channels} channels and threads looked at, {Messages} messages fetched", guildId, starts.Count, fetched);
        }
    }

    private (ulong GuildId, HashSet<ulong> Live)? TakeCatchUp()
    {
        lock (_catchUps)
        {
            if (_catchUps.Count == 0)
                return null;
            var (guildId, live) = _catchUps.First();
            _catchUps.Remove(guildId);
            return (guildId, live);
        }
    }

    // Adds every channel and thread that holds messages and isn't known yet. The listing refreshes the
    // archive's channel table too, since it's in hand.
    private async Task DiscoverAsync(ulong guildId, CancellationToken ct)
    {
        var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);
        var list = await GuildChannels.ListAsync(rest, guildId, ct);
        archiver.OnChannelsListed(guildId, list);
        var found = list.Channels.OfType<TextGuildChannel>().ToDictionary(c => c.Id, c => c.Name);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.BackfillChannels.Where(c => c.GuildId == guildId).Select(c => c.ChannelId).ToHashSetAsync(ct);
        foreach (var (id, name) in found.Where(f => !known.Contains(f.Key) && !rules.ExcludedChannelIds.Contains(f.Key)))
            db.BackfillChannels.Add(new() { ChannelId = id, GuildId = guildId, Name = name });
        await db.SaveChangesAsync(ct);
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
            var (page, problem) = await FetchPageAsync(channel.GuildId, channel.ChannelId, new() { From = channel.Cursor, Direction = PaginationDirection.Before, BatchSize = PageSize }, ct);
            if (page is null)
            {
                await SaveAsync(channel, done: true, problem, ct);
                return;
            }

            channel.Fetched += page.Count;
            if (page.Count > 0)
                channel.Cursor = page.Min(m => m.Id);

            var done = page.Count < PageSize;
            await SaveAsync(channel, done, problem: null, ct);
            if (done || !await IsRunningAsync(channel.GuildId, ct))
                return;
        }
    }

    // Forward from a message, that one included, to the newest; how many were fetched.
    private async Task<int> FetchForwardAsync(ulong guildId, ulong channelId, ulong start, CancellationToken ct)
    {
        var after = start - 1;
        var fetched = 0;
        while (true)
        {
            var (page, _) = await FetchPageAsync(guildId, channelId, new() { From = after, Direction = PaginationDirection.After, BatchSize = PageSize }, ct);
            if (page is null)
                return fetched;
            fetched += page.Count;
            if (page.Count < PageSize)
                return fetched;
            after = page.Max(m => m.Id);
        }
    }

    // A page handed to the Archiver, then its ids for the reconciliation; or why there is none: the channel
    // can't be read, or is gone.
    private async Task<(List<RestMessage>? Messages, string? Problem)> FetchPageAsync(ulong guildId, ulong channelId, PaginationProperties<ulong> page, CancellationToken ct)
    {
        while (archiver.Pending > MaxPending || archiver.ChecksPending > MaxChecks)
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

        List<RestMessage> messages;
        try
        {
            messages = await rest.GetMessagesAsync(channelId, page).Take(PageSize).ToListAsync(ct);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return (null, ex.StatusCode == HttpStatusCode.Forbidden ? "no access" : "gone");
        }

        foreach (var message in messages)
            archiver.OnFetched(guildId, message);
        archiver.OnPageFetched(guildId, channelId, messages.Select(m => m.Id).ToList());
        return (messages, null);
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
