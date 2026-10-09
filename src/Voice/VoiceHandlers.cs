using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Stats;

namespace THOBOTTO.Voice;

public sealed class VoiceStateHandler(VoicePresence presence, DynamicVoice voice, VoiceLog log) : IVoiceStateUpdateGatewayHandler
{
    public async ValueTask HandleAsync(VoiceState arg)
    {
        presence.Record(arg);
        voice.Changed(arg.GuildId);
        await log.RecordAsync(arg);
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
