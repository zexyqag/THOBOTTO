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
// resume) pages forward in every channel the archive has messages of, from the last page it holds, and through
// the channels and threads made since the last message it archived, so what happened while the bot was away is
// caught: new messages, edits, deletions, reactions. A channel the catch-up fails in is tried again a few times.
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

    private const int CatchUpTries = 3;

    private readonly SemaphoreSlim _wake = new(0, 1);

    // A server to catch up on: the channels and threads live in it now, when it was first asked for (what was
    // archived before then is what the bot was away from), and how often it was tried.
    private sealed record CatchUpRequest(HashSet<ulong> Live, DateTimeOffset RequestedAt, int Tried);

    private readonly Dictionary<ulong, CatchUpRequest> _catchUps = [];

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
        Request(guildId, liveChannelIds, time.GetUtcNow(), tried: 0);
        Wake();
    }

    private void Request(ulong guildId, IEnumerable<ulong> liveChannelIds, DateTimeOffset requestedAt, int tried)
    {
        lock (_catchUps)
        {
            var pending = _catchUps.GetValueOrDefault(guildId);
            var live = pending?.Live ?? [];
            live.UnionWith(liveChannelIds);
            _catchUps[guildId] = new(live, pending is null || requestedAt < pending.RequestedAt ? requestedAt : pending.RequestedAt, Math.Max(pending?.Tried ?? 0, tried));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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
        while (TakeCatchUp() is { } taken)
        {
            var (guildId, request) = taken;
            if (!await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
                continue;
            var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);
            var wanted = request.Live.Where(id => !rules.ExcludedChannelIds.Contains(id)).ToHashSet();

            // Each channel from the last page the archive holds, so edits and deletions in it are seen too. A
            // channel or thread made after the last message archived before the request has no page: it's read
            // from its first message.
            var starts = new List<(ulong ChannelId, ulong After)>();
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                var archived = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().ToListAsync(ct);
                foreach (var channelId in archived.Where(wanted.Contains))
                {
                    var newest = await db.ArchivedMessages.Where(m => m.ChannelId == channelId).OrderByDescending(m => m.Id).Select(m => m.Id).Take(PageSize).ToListAsync(ct);
                    starts.Add((channelId, newest[^1] - 1));
                }
                var before = Snowflake.Create(request.RequestedAt);
                if (await db.ArchivedMessages.Where(m => m.GuildId == guildId && m.Id < before).MaxAsync(m => (ulong?)m.Id, ct) is { } last)
                    starts.AddRange(wanted.Where(id => id > last && !archived.Contains(id)).Order().Select(id => (id, 0UL)));
            }

            var fetched = 0;
            var failed = new List<ulong>();
            foreach (var (channelId, after) in starts)
            {
                try
                {
                    fetched += await FetchForwardAsync(guildId, channelId, after, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failed.Add(channelId);
                    logger.LogWarning(ex, "Catching up on channel {ChannelId} of {GuildId} failed", channelId, guildId);
                }
            }
            // The loop comes round in a minute; not woken, so Discord gets a moment.
            if (failed.Count > 0 && request.Tried + 1 < CatchUpTries)
                Request(guildId, failed, request.RequestedAt, request.Tried + 1);
            logger.LogInformation("Catch-up of {GuildId}: {Channels} channels and threads looked at, {Messages} messages fetched, {Failed} failed", guildId, starts.Count, fetched, failed.Count);
        }
    }

    private (ulong GuildId, CatchUpRequest Request)? TakeCatchUp()
    {
        lock (_catchUps)
        {
            if (_catchUps.Count == 0)
                return null;
            var (guildId, request) = _catchUps.First();
            _catchUps.Remove(guildId);
            return (guildId, request);
        }
    }

    // Adds every channel and thread that holds messages and isn't known yet. The listing refreshes the
    // archive's channel table too, since it's in hand.
    private async Task DiscoverAsync(ulong guildId, CancellationToken ct)
    {
        var rules = await settings.GetAsync<ArchiveRules>(guildId, Archiver.ModuleId);
        var startedAt = time.GetUtcNow();
        var list = await GuildChannels.ListAsync(rest, guildId, ct);
        archiver.OnChannelsListed(guildId, list, startedAt);
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
            var (page, problem) = await FetchPageAsync(channel.GuildId, channel.ChannelId, PaginationDirection.Before, channel.Cursor, ct);
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

    // Forward from after a message (0: from the first) to the newest; how many were fetched.
    private async Task<int> FetchForwardAsync(ulong guildId, ulong channelId, ulong after, CancellationToken ct)
    {
        var fetched = 0;
        while (true)
        {
            var (page, _) = await FetchPageAsync(guildId, channelId, PaginationDirection.After, after, ct);
            if (page is null)
                return fetched;
            fetched += page.Count;
            if (page.Count < PageSize)
                return fetched;
            after = page.Max(m => m.Id);
        }
    }

    // A page from the cursor (null: the newest) handed to the Archiver, then its ids and the range it vouches for,
    // for the reconciliation; or why there is none: the channel can't be read, or is gone.
    private async Task<(List<RestMessage>? Messages, string? Problem)> FetchPageAsync(ulong guildId, ulong channelId, PaginationDirection direction, ulong? cursor, CancellationToken ct)
    {
        while (archiver.Pending > MaxPending || archiver.ChecksPending > MaxChecks)
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var askedAt = time.GetUtcNow();
        List<RestMessage> messages;
        try
        {
            messages = await rest.GetMessagesAsync(channelId, new() { From = cursor, Direction = direction, BatchSize = PageSize }).Take(PageSize).ToListAsync(ct);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return (null, ex.StatusCode == HttpStatusCode.Forbidden ? "no access" : "gone");
        }

        foreach (var message in messages)
            archiver.OnFetched(guildId, message, askedAt);
        var ids = messages.Select(m => m.Id).ToList();
        var (from, to) = ArchiveSync.Covered(direction, cursor, ids, full: messages.Count == PageSize, askedAt);
        archiver.OnPageFetched(guildId, channelId, ids, from, to);
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
