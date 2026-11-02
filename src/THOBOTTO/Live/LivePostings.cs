using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Live;

public sealed class LivePostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var channel = (await settings.GetAsync<LiveRules>(guild.Id, LiveWatch.ModuleId)).ChannelId;
        return [new(LiveWatch.ModuleId, "Who's live", channel is { } own ? new Landing.In(own) : new Landing.Nowhere("only an overview's Live now block shows it, until it has a channel"), Posting.Settings(guild.Id, LiveWatch.ModuleId))];
    }
}
