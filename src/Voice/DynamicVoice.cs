using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice;

// Gateway handlers record voice states in VoicePresence, in event order, and queue the guild.
// One loop then works from that record, so decisions never race each other or the
// gateway cache (NetCord updates it after handlers are invoked).
public sealed class DynamicVoice(
    RestClient rest,
    GatewayClient gateway,
    VoicePresence presence,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    TimeProvider time,
    ILogger<DynamicVoice> logger) : BackgroundService
{
    public const string ModuleId = "voice";

    private readonly System.Threading.Channels.Channel<ulong> _queue = System.Threading.Channels.Channel.CreateUnbounded<ulong>(new() { SingleReader = true });

    // Members moved into a new channel whose arrival hasn't been seen yet; until then
    // they still appear in the hub and the new channel appears empty. Loop-only.
    private readonly Dictionary<(ulong GuildId, ulong UserId), ulong> _moving = [];

    public void Changed(ulong guildId) => _queue.Writer.TryWrite(guildId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var guildId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ReconcileAsync(guildId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reconciling voice channels in guild {GuildId} failed", guildId);
            }
        }
    }

    private async Task ReconcileAsync(ulong guildId, CancellationToken ct)
    {
        var where = presence.Snapshot(guildId).ToDictionary(p => p.Key, p => p.Value.ChannelId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hubs = await db.VoiceHubs.Where(h => h.GuildId == guildId).Select(h => h.ChannelId).ToHashSetAsync(ct);

        foreach (var key in _moving.Keys.Where(k => k.GuildId == guildId).ToList())
            if (!where.TryGetValue(key.UserId, out var channelId) || !hubs.Contains(channelId))
                _moving.Remove(key);

        if (await modules.IsEnabledAsync(guildId, ModuleId))
        {
            foreach (var (userId, hubId) in where.Where(w => hubs.Contains(w.Value)))
            {
                if (!_moving.ContainsKey((guildId, userId)) && await CreateAsync(db, guildId, userId, hubId, ct) is { } created)
                    _moving[(guildId, userId)] = created;
            }
        }

        var occupied = where.Values
            .Concat(_moving.Where(m => m.Key.GuildId == guildId).Select(m => m.Value))
            .ToHashSet();
        var tracked = await db.DynamicVoiceChannels
            .Where(c => c.GuildId == guildId)
            .Select(c => c.ChannelId)
            .ToListAsync(ct);

        foreach (var channelId in tracked.Where(id => !occupied.Contains(id)))
        {
            try
            {
                await rest.DeleteChannelAsync(channelId, cancellationToken: ct);
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }

            // The channel-delete event may already have removed the row.
            await db.DynamicVoiceChannels.Where(c => c.ChannelId == channelId).ExecuteDeleteAsync(ct);
        }
    }

    // Returns the new channel if the member was moved into it.
    private async Task<ulong?> CreateAsync(BotDbContext db, ulong guildId, ulong userId, ulong hubId, CancellationToken ct)
    {
        if (!gateway.Cache.Guilds.TryGetValue(guildId, out var guild)
            || !guild.Channels.TryGetValue(hubId, out var hubChannel)
            || hubChannel is not IVoiceGuildChannel hub)
            return null;

        var member = await rest.GetGuildUserAsync(guildId, userId, cancellationToken: ct);
        var name = $"{member.Nickname ?? member.GlobalName ?? member.Username}'s channel";

        var channel = await rest.CreateGuildChannelAsync(guildId, new(name, ChannelType.VoiceGuildChannel)
        {
            ParentId = hub.ParentId,
            Bitrate = hub.Bitrate,
            PermissionOverwrites = hub.PermissionOverwrites.Values
                .Select(o => new PermissionOverwriteProperties(o.Id, o.Type) { Allowed = o.Allowed, Denied = o.Denied }),
        }, cancellationToken: ct);

        // Tracked before the move, so a failed move leaves an empty channel that gets cleaned up.
        db.DynamicVoiceChannels.Add(new()
        {
            ChannelId = channel.Id,
            GuildId = guildId,
            OwnerId = userId,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await rest.ModifyGuildUserAsync(guildId, userId, u => u.ChannelId = channel.Id, cancellationToken: ct);
            return channel.Id;
        }
        catch (RestException ex)
        {
            // Usually the member left the hub before the move.
            logger.LogInformation(ex, "Moving user {UserId} into new voice channel {ChannelId} failed", userId, channel.Id);
            return null;
        }
    }
}
