using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Stats;

// Members' time in voice channels (bots left out): a session per channel, from joining to leaving or moving.
public sealed class VoiceLog(IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<VoiceLog> logger) : BackgroundService
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(1);

    // (guild, user) → (session, channel) for everyone in voice now.
    private readonly ConcurrentDictionary<(ulong Guild, ulong User), (long Session, ulong Channel)> _open = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The bot (re)joined a guild: sessions left open by a crash end when last seen; whoever is in voice now starts one.
    public Task SeedAsync(Guild guild) => WithGateAsync(async db =>
    {
        var now = time.GetUtcNow();
        foreach (var key in _open.Keys.Where(k => k.Guild == guild.Id).ToList())
            _open.TryRemove(key, out _);
        await db.VoiceSessions.Where(s => s.GuildId == guild.Id && s.LeftAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.LeftAt, v => v.SeenAt));
        foreach (var state in guild.VoiceStates.Values.Where(v => v.ChannelId.HasValue && guild.Users.TryGetValue(v.UserId, out var u) && !u.IsBot))
            await OpenAsync(db, guild.Id, state.UserId, state.ChannelId!.Value, now);
    });

    public Task RecordAsync(VoiceState state) => state.User?.IsBot == true ? Task.CompletedTask : WithGateAsync(async db =>
    {
        var key = (state.GuildId, state.UserId);
        var now = time.GetUtcNow();
        if (_open.TryGetValue(key, out var open))
        {
            // Muting, deafening and the like don't change the channel.
            if (open.Channel == state.ChannelId)
                return;
            await CloseAsync(db, key, now);
        }
        if (state.ChannelId is { } channelId)
            await OpenAsync(db, state.GuildId, state.UserId, channelId, now);
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Heartbeat, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await WithGateAsync(db => db.VoiceSessions.Where(s => s.LeftAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.SeenAt, time.GetUtcNow())));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Updating open voice sessions failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var now = time.GetUtcNow();
        await WithGateAsync(db => db.VoiceSessions.Where(s => s.LeftAt == null).ExecuteUpdateAsync(s => s.SetProperty(v => v.LeftAt, now).SetProperty(v => v.SeenAt, now)));
        _open.Clear();
    }

    private async Task OpenAsync(BotDbContext db, ulong guildId, ulong userId, ulong channelId, DateTimeOffset now)
    {
        var session = new VoiceSession { GuildId = guildId, UserId = userId, ChannelId = channelId, JoinedAt = now, SeenAt = now };
        db.VoiceSessions.Add(session);
        await db.SaveChangesAsync();
        _open[(guildId, userId)] = (session.Id, channelId);
    }

    private async Task CloseAsync(BotDbContext db, (ulong Guild, ulong User) key, DateTimeOffset now)
    {
        if (_open.TryRemove(key, out var open))
            await db.VoiceSessions.Where(s => s.Id == open.Session).ExecuteUpdateAsync(s => s.SetProperty(v => v.LeftAt, now).SetProperty(v => v.SeenAt, now));
    }

    private async Task WithGateAsync(Func<BotDbContext, Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await action(db);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            logger.LogWarning(ex, "Recording voice time failed");
        }
        finally
        {
            _gate.Release();
        }
    }
}
