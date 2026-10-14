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
// the channels and threads made since the last message it archived before, so what happened while the bot was
// away is caught: new messages, edits, deletions, reactions. A channel the catch-up fails in is tried again a
// few times, a minute apart.
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
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _wake = new(0, 1);

    // A server to catch up on: the channels and threads live in it now with when they were made, when it was
    // first asked for (what was archived before then is what the bot was away from), how often it was tried and
    // not before when, and the newest message archived before the request as the first try found it.
    private sealed record CatchUpRequest(Dictionary<ulong, DateTimeOffset> Live, DateTimeOffset RequestedAt, int Tried, DateTimeOffset NotBefore, ulong? Last);

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

    // The channels and threads a message can be fetched from now, each with when it was made: a thread's own
    // time, later than its id says when it was started from an older message.
    public static IReadOnlyDictionary<ulong, DateTimeOffset> LiveChannels(Guild guild)
    {
        var live = guild.Channels.Values.OfType<TextGuildChannel>().ToDictionary(c => c.Id, c => Snowflake.Timestamp(c.Id));
        foreach (var thread in guild.ActiveThreads.Values)
        {
            var byId = Snowflake.Timestamp(thread.Id);
            live[thread.Id] = thread.Metadata.CreateTimestamp > byId ? thread.Metadata.CreateTimestamp : byId;
        }
        return live;
    }

    public void CatchUp(ulong guildId, IReadOnlyDictionary<ulong, DateTimeOffset> live)
    {
        var now = time.GetUtcNow();
        Request(guildId, live, now, tried: 0, notBefore: now, last: null);
        Wake();
    }

    // A fresh request joins a pending one: all its channels, the earlier times, the fewer tries.
    private void Request(ulong guildId, IReadOnlyDictionary<ulong, DateTimeOffset> live, DateTimeOffset requestedAt, int tried, DateTimeOffset notBefore, ulong? last)
    {
        lock (_catchUps)
        {
            var pending = _catchUps.GetValueOrDefault(guildId);
            var all = pending?.Live ?? [];
            foreach (var (id, made) in live)
                all[id] = made;
            _catchUps[guildId] = pending is null
                ? new(all, requestedAt, tried, notBefore, last)
                : new(all, pending.RequestedAt < requestedAt ? pending.RequestedAt : requestedAt, Math.Min(pending.Tried, tried), pending.NotBefore < notBefore ? pending.NotBefore : notBefore, pending.Last ?? last);
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
            var wanted = request.Live.Where(kv => !rules.ExcludedChannelIds.Contains(kv.Key)).ToDictionary();

            var starts = new List<(ulong ChannelId, ulong After)>();
            ulong? last;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                // The newest message archived before the request, less the clocks' slack: a channel or thread made
                // after it was made while the bot was away, and is read from its first message. The first try's
                // answer holds for the retries, whose fetches moved it.
                var before = Snowflake.Create(request.RequestedAt - ArchiveSync.Slack);
                last = request.Last ?? await db.ArchivedMessages.Where(m => m.GuildId == guildId && m.Id < before).MaxAsync(m => (ulong?)m.Id, ct);
                var madeAfter = last is { } since ? Snowflake.Timestamp(since) : DateTimeOffset.MaxValue;
                var archived = await db.ArchivedMessages.Where(m => m.GuildId == guildId).Select(m => m.ChannelId).Distinct().ToListAsync(ct);

                // Each other channel from the page before the newest the archive holds, so edits and deletions in it are seen too.
                foreach (var (channelId, made) in wanted.OrderBy(kv => kv.Key))
                {
                    if (made > madeAfter)
                        starts.Add((channelId, 0));
                    else if (archived.Contains(channelId))
                    {
                        var newest = await db.ArchivedMessages.Where(m => m.ChannelId == channelId).OrderByDescending(m => m.Id).Select(m => m.Id).Take(PageSize).ToListAsync(ct);
                        starts.Add((channelId, newest[^1] - 1));
                    }
                }
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
            if (failed.Count > 0 && request.Tried + 1 < CatchUpTries)
                Request(guildId, failed.ToDictionary(id => id, id => wanted[id]), request.RequestedAt, request.Tried + 1, time.GetUtcNow() + RetryAfter, last);
            logger.LogInformation("Catch-up of {GuildId}: {Channels} channels and threads looked at, {Messages} messages fetched, {Failed} failed", guildId, starts.Count, fetched, failed.Count);
        }
    }

    // The first request whose time has come.
    private (ulong GuildId, CatchUpRequest Request)? TakeCatchUp()
    {
        lock (_catchUps)
        {
            var now = time.GetUtcNow();
            if (_catchUps.FirstOrDefault(kv => kv.Value.NotBefore <= now) is not { Value: { } request } ready)
                return null;
            _catchUps.Remove(ready.Key);
            return (ready.Key, request);
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

        // A channel the bot can't read the history of answers with nothing, not a refusal; a page with nothing is
        // trusted only when the channel shows it has any message at all.
        if (messages.Count == 0 && !await rest.GetMessagesAsync(channelId, new() { BatchSize = 1 }).Take(1).AnyAsync(ct))
            return (messages, null);

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
