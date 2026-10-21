using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Channels;

public sealed class VoiceChannelDeleteHandler(IDbContextFactory<BotDbContext> dbFactory, SettingsStore settings, GatewayClient gateway) : IGuildChannelDeleteGatewayHandler
{
    public async ValueTask HandleAsync(IGuildChannel arg)
    {
        var rules = await settings.GetAsync<VoiceRules>(arg.GuildId, DynamicVoice.ModuleId);
        if (rules.HubChannelIds.Contains(arg.Id))
            await settings.SetAsync(arg.GuildId, DynamicVoice.ModuleId, rules with { HubChannelIds = rules.HubChannelIds.Where(h => h != arg.Id).ToList() }, gateway.Cache.User!.Id, $"hub channel deleted: {arg.Id}");
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.DynamicVoiceChannels.Where(c => c.ChannelId == arg.Id).ExecuteDeleteAsync();
    }
}
