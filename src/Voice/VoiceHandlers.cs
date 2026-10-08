using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Voice;

public sealed class VoiceStateHandler(VoicePresence presence, DynamicVoice voice) : IVoiceStateUpdateGatewayHandler
{
    public ValueTask HandleAsync(VoiceState arg)
    {
        presence.Record(arg);
        voice.Changed(arg.GuildId);
        return default;
    }
}

public sealed class VoiceGuildCreateHandler(VoicePresence presence, DynamicVoice voice) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        if (arg.Guild is { } guild)
        {
            presence.Seed(guild);
            voice.Changed(guild.Id);
        }
        return default;
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
