using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Modules;

namespace THOBOTTO.GameServers;

// Polls each guild's game servers on its own interval and keeps what it found, for the overview channels' block, the
// Game servers page and the games' sessions.
public sealed class ServerWatch(
    GameDig gameDig,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    TimeProvider time,
    ILogger<ServerWatch> logger) : BackgroundService
{
    public const string ModuleId = "game-servers";

    // How often the loop checks which guilds are due; each guild's PollSeconds decides when it is.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastPoll = new();
    private readonly ConcurrentDictionary<long, (ServerStatus? Status, int Failures)> _last = new();

    // What the last poll found, or null when the server was offline or hasn't been polled yet.
    public ServerStatus? LastStatus(long serverId)
        => _last.TryGetValue(serverId, out var last) && last.Failures == 0 ? last.Status : null;

    public async Task<ServerSettings> GetSettingsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServerSettings.FindAsync(guildId) ?? new() { GuildId = guildId };
    }

    public async Task UpdateSettingsAsync(ulong guildId, ulong actorId, string details, Action<ServerSettings> change)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var settings = await db.ServerSettings.FindAsync(guildId);
            if (settings is null)
                db.ServerSettings.Add(settings = new() { GuildId = guildId });
            change(settings);
            db.AuditEntries.Add(Audit(guildId, actorId, "servers.settings", details));
            await db.SaveChangesAsync();
        }
        Refresh(guildId);
    }

    public async Task AddAsync(GameServer server)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.GameServers.Add(server);
            db.AuditEntries.Add(Audit(server.GuildId, server.OwnerId, "servers.add", $"{server.Game} {server.Address}"));
            await db.SaveChangesAsync();
        }
        Refresh(server.GuildId);
    }

    // Changes a server's details (not its address: that's a new server); false when it's gone.
    public async Task<bool> UpdateAsync(long serverId, ulong actorId, Action<GameServer> change)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (await db.GameServers.FindAsync(serverId) is not { } server)
                return false;
            change(server);
            db.AuditEntries.Add(Audit(server.GuildId, actorId, "servers.edit", $"{server.Game} {server.Address}"));
            await db.SaveChangesAsync();
        }
        return true;
    }

    public async Task RemoveAsync(long serverId, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.GameServers.FindAsync(serverId) is not { } server)
            return;
        db.GameServers.Remove(server);
        db.AuditEntries.Add(Audit(server.GuildId, actorId, "servers.remove", $"{server.Game} {server.Address}"));
        await db.SaveChangesAsync();
        _last.TryRemove(serverId, out _);
    }

    private void Refresh(ulong guildId)
    {
        _lastPoll.TryRemove(guildId, out _);
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RenameOldGameIdsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checking the game servers failed");
            }

            await _wake.WaitAsync(Tick, stoppingToken);
        }
    }

    // GameDig renames game ids between major versions; it keeps the old id alongside the new one.
    private async Task RenameOldGameIdsAsync(CancellationToken ct)
    {
        // Also the startup check that GameDig runs at all: a failure here stops the host.
        var games = await gameDig.GetGamesAsync();
        logger.LogInformation("GameDig knows {Count} games", games.Count);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var server in await db.GameServers.ToListAsync(ct))
        {
            var game = await gameDig.FindGameAsync(server.Game);
            if (game is null)
                logger.LogWarning("Server {ServerId} in {GuildId} uses game id {Game}, which GameDig doesn't know", server.Id, server.GuildId, server.Game);
            else if (game.Id != server.Game)
            {
                logger.LogInformation("Server {ServerId} in {GuildId}: game id {Old} is now {New}", server.Id, server.GuildId, server.Game, game.Id);
                server.Game = game.Id;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PollDueAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var guilds = await db.GameServers.Select(s => s.GuildId).Distinct().ToListAsync(ct);
        var now = time.GetUtcNow();

        foreach (var guildId in guilds)
        {
            var settings = await db.ServerSettings.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct) ?? new() { GuildId = guildId };
            if (_lastPoll.TryGetValue(guildId, out var last) && now - last < TimeSpan.FromSeconds(settings.PollSeconds))
                continue;
            if (!await modules.IsEnabledAsync(guildId, ModuleId))
                continue;

            _lastPoll[guildId] = now;
            // The queries' lines (GameDig's) are this server's.
            using var _ = logger.BeginScope(DebugLog.Guild(guildId));

            var servers = await db.GameServers.AsNoTracking().Where(s => s.GuildId == guildId).ToListAsync(ct);
            var results = new ConcurrentDictionary<long, ServerStatus?>();
            await Parallel.ForEachAsync(servers, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (server, token) => results[server.Id] = await gameDig.QueryAsync(server.Game, server.Host, server.Port, token));

            foreach (var server in servers)
                Smooth(server.Id, results[server.Id], settings.FailuresBeforeOffline);
        }
    }

    private void Smooth(long serverId, ServerStatus? result, int failuresBeforeOffline)
    {
        _last.AddOrUpdate(serverId,
            _ => (result, result is null ? failuresBeforeOffline : 0),
            (_, previous) => result is null ? (previous.Status, previous.Failures + 1) : (result, 0));
    }

    private AuditEntry Audit(ulong guildId, ulong actorId, string action, string details) => new()
    {
        GuildId = guildId,
        ActorId = actorId,
        Action = action,
        Details = details,
        CreatedAt = time.GetUtcNow(),
    };
}
