using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;

namespace THOBOTTO.Voice.Channels;

public sealed class VoiceChannelDeleteHandler(IDbContextFactory<BotDbContext> dbFactory) : IGuildChannelDeleteGatewayHandler
{
    public async ValueTask HandleAsync(IGuildChannel arg)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.VoiceHubs.Where(h => h.ChannelId == arg.Id).ExecuteDeleteAsync();
        await db.DynamicVoiceChannels.Where(c => c.ChannelId == arg.Id).ExecuteDeleteAsync();
    }
}
