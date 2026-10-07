using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Voice;

public sealed class VoiceStateHandler(DynamicVoice voice) : IVoiceStateUpdateGatewayHandler
{
    public ValueTask HandleAsync(VoiceState arg)
    {
        voice.Record(arg);
        return default;
    }
}

public sealed class VoiceGuildCreateHandler(DynamicVoice voice) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        if (arg.Guild is { } guild)
            voice.Seed(guild);
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
