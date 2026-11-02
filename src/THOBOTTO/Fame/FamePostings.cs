using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Fame;

public sealed class FamePostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var showcase = (await settings.GetAsync<FameRules>(guild.Id, HallOfFame.ModuleId)).ShowcaseChannelId;
        return [new(HallOfFame.ModuleId, "Highlights", showcase is { } channel ? new Landing.In(channel) : new Landing.Nowhere("nothing is tracked until a showcase channel is set"), Posting.Settings(guild.Id, HallOfFame.ModuleId))];
    }
}
