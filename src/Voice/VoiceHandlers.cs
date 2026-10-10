using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Stats;

namespace THOBOTTO.Voice;

public sealed partial class VoiceStateHandler(VoicePresence presence, DynamicVoice voice, VoiceLog log, THOBOTTO.Sounds.SoundBoard sounds, ILogger<VoiceStateHandler> logger) : IVoiceStateUpdateGatewayHandler
{
    public async ValueTask HandleAsync(VoiceState arg)
    {
        var before = presence.Snapshot(arg.GuildId).GetValueOrDefault(arg.UserId)?.ChannelId;
        presence.Record(arg);
        // Arriving in a channel plays their join sound, without holding up the rest.
        if (arg.ChannelId is { } channelId && channelId != before && arg.User?.IsBot != true)
            _ = JoinSoundAsync(arg.GuildId, arg.UserId, channelId);
        voice.Changed(arg.GuildId);
        await log.RecordAsync(arg);
    }
}

public sealed partial class VoiceStateHandler
{
    private async Task JoinSoundAsync(ulong guildId, ulong userId, ulong channelId)
    {
        try
        {
            await sounds.JoinedAsync(guildId, userId, channelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A join sound in {ChannelId} failed: {Message}", channelId, ex.Message);
        }
    }
}

public sealed class VoiceGuildCreateHandler(VoicePresence presence, DynamicVoice voice, VoiceLog log) : IGuildCreateGatewayHandler
{
    public async ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        if (arg.Guild is { } guild)
        {
            presence.Seed(guild);
            voice.Changed(guild.Id);
            await log.SeedAsync(guild);
        }
    }
}

public sealed class VoiceChannelDeleteHandler(IDbContextFactory<BotDbContext> dbFactory) : IGuildChannelDeleteGatewayHandler
{
    public async ValueTask HandleAsync(IGuildChannel arg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.VoiceHubs.Where(h => h.ChannelId == arg.Id).ExecuteDeleteAsync();
        await db.DynamicVoiceChannels.Where(c => c.ChannelId == arg.Id).ExecuteDeleteAsync();
    }
}
