using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.GameServers;

// Polls each guild's servers on its own interval and keeps one status message per server
// on its board. Changes to servers and settings go through here too, under the same gate
// as publishing, so a removal can't race a message edit.
public sealed class ServerBoardService(
    RestClient rest,
    GameDig gameDig,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    TimeProvider time,
    ILogger<ServerBoardService> logger) : BackgroundService
{
    public const string ModuleId = "servers";

    // How often the loop checks which guilds are due; each guild's PollSeconds decides when it is.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    private const int MaxPlayerNames = 30;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastPoll = new();
    private readonly ConcurrentDictionary<long, (ServerStatus? Status, int Failures)> _last = new();

    // What each message currently shows, to skip edits that change nothing.
    private readonly ConcurrentDictionary<long, string> _shown = new();

    // What the last poll found, or null when the server was offline or hasn't been polled yet.
    public ServerStatus? LastStatus(long serverId)
        => _last.TryGetValue(serverId, out var last) && last.Failures == 0 ? last.Status : null;

    public async Task<ServerSettings> GetSettingsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ServerSettings.FindAsync(guildId) ?? new() { GuildId = guildId };
    }

    // Applies a change to the guild's settings, moving the board's messages if the channel changed.
    public async Task UpdateSettingsAsync(ulong guildId, ulong actorId, string details, Action<ServerSettings> change)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var settings = await db.ServerSettings.FindAsync(guildId);
            if (settings is null)
                db.ServerSettings.Add(settings = new() { GuildId = guildId });

            var oldChannel = settings.BoardChannelId;
            change(settings);

            if (oldChannel is { } old && old != settings.BoardChannelId)
            {
                foreach (var server in await db.GameServers.Where(s => s.GuildId == guildId && s.MessageId != null).ToListAsync())
                {
                    await DeleteMessageAsync(old, server.MessageId!.Value);
                    server.MessageId = null;
                    _shown.TryRemove(server.Id, out _);
                }
            }

            db.AuditEntries.Add(Audit(guildId, actorId, "servers.settings", details));
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }

        Refresh(guildId);
    }

    public async Task AddAsync(GameServer server)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.GameServers.Add(server);
            db.AuditEntries.Add(Audit(server.GuildId, server.OwnerId, "servers.add", $"{server.Game} {server.Address}"));
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }

        Refresh(server.GuildId);
    }

    public async Task RemoveAsync(long serverId, ulong actorId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var server = await db.GameServers.FindAsync(serverId);
            if (server is null)
                return;

            if (server.MessageId is { } messageId
                && await db.ServerSettings.FindAsync(server.GuildId) is { BoardChannelId: { } channelId })
                await DeleteMessageAsync(channelId, messageId);

            db.GameServers.Remove(server);
            db.AuditEntries.Add(Audit(server.GuildId, actorId, "servers.remove", $"{server.Game} {server.Address}"));
            await db.SaveChangesAsync();

            _shown.TryRemove(serverId, out _);
            _last.TryRemove(serverId, out _);
        }
        finally
        {
            _gate.Release();
        }
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
                logger.LogError(ex, "Updating the server boards failed");
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
                logger.LogWarning("Server {ServerId} uses game id {Game}, which GameDig doesn't know", server.Id, server.Game);
            else if (game.Id != server.Game)
            {
                logger.LogInformation("Server {ServerId}: game id {Old} is now {New}", server.Id, server.Game, game.Id);
                server.Game = game.Id;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PollDueAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var boards = await db.ServerSettings.AsNoTracking().Where(s => s.BoardChannelId != null).ToListAsync(ct);
        var now = time.GetUtcNow();

        foreach (var settings in boards)
        {
            if (_lastPoll.TryGetValue(settings.GuildId, out var last) && now - last < TimeSpan.FromSeconds(settings.PollSeconds))
                continue;
            if (!await modules.IsEnabledAsync(settings.GuildId, ModuleId))
                continue;

            _lastPoll[settings.GuildId] = now;

            var servers = await db.GameServers.AsNoTracking().Where(s => s.GuildId == settings.GuildId).ToListAsync(ct);
            var results = new ConcurrentDictionary<long, ServerStatus?>();
            await Parallel.ForEachAsync(servers, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (server, token) => results[server.Id] = await gameDig.QueryAsync(server.Game, server.Host, server.Port, token));

            foreach (var server in servers)
            {
                var status = Smooth(server.Id, results[server.Id], settings.FailuresBeforeOffline);
                var gameName = (await gameDig.FindGameAsync(server.Game))?.Name ?? server.Game;
                await PublishAsync(settings.BoardChannelId!.Value, server.Id, Render(server, gameName, status), ct);
            }
        }
    }

    private ServerStatus? Smooth(long serverId, ServerStatus? result, int failuresBeforeOffline)
    {
        var (status, failures) = _last.AddOrUpdate(serverId,
            _ => (result, result is null ? failuresBeforeOffline : 0),
            (_, previous) => result is null ? (previous.Status, previous.Failures + 1) : (result, 0));

        return failures >= failuresBeforeOffline ? null : status;
    }

    private async Task PublishAsync(ulong channelId, long serverId, EmbedProperties embed, CancellationToken ct)
    {
        var shown = $"{embed.Title}\n{embed.Color.RawValue}\n{embed.Description}";

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var server = await db.GameServers.FindAsync([serverId], ct);
            if (server is null)
                return;

            if (server.MessageId is not null && _shown.TryGetValue(serverId, out var previous) && previous == shown)
                return;

            embed.Timestamp = time.GetUtcNow();

            if (server.MessageId is { } messageId)
            {
                try
                {
                    await rest.ModifyMessageAsync(channelId, messageId, m => m.Embeds = [embed], cancellationToken: ct);
                    _shown[serverId] = shown;
                    return;
                }
                catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                }
            }

            var message = await rest.SendMessageAsync(channelId, new() { Embeds = [embed] }, cancellationToken: ct);
            server.MessageId = message.Id;
            await db.SaveChangesAsync(ct);
            _shown[serverId] = shown;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static EmbedProperties Render(GameServer server, string gameName, ServerStatus? status)
    {
        var text = new StringBuilder();
        if (status is null)
            text.AppendLine("**Offline**");
        else
        {
            text.Append($"**Online**: {status.Players}/{status.MaxPlayers} players");
            text.AppendLine(status.Password ? ", password protected" : "");
        }

        text.AppendLine($"Game: {gameName}");
        if (status?.Map is { } map)
            text.AppendLine($"Map: {Escape(map)}");
        text.AppendLine($"Address: `{server.Address}`");

        if (status is { PlayerNames.Count: > 0 })
        {
            var names = string.Join(", ", status.PlayerNames.Take(MaxPlayerNames).Select(Escape));
            var more = status.PlayerNames.Count - MaxPlayerNames;
            text.AppendLine($"On now: {names}{(more > 0 ? $" and {more} more" : "")}");
        }

        return new()
        {
            Title = Escape(server.Name ?? status?.Name ?? gameName),
            Description = text.ToString(),
            Color = status is null ? new(0x99AAB5) : new(0x57F287),
        };
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    private async Task DeleteMessageAsync(ulong channelId, ulong messageId)
    {
        try
        {
            await rest.DeleteMessageAsync(channelId, messageId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
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
